/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Core.Transport;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// The realtime channel reads frames from a server it must not trust. These tests deliver every malformed shape through
/// a scripted socket and require the same protocol-violation failure for each, with the connection torn down and any
/// waiting send released for replay; they also pin what a well-formed ACK, ERROR and ordinary frame do.
/// </summary>
public sealed class RealtimeProtocolTests
{
   private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);



   /// <summary>
   /// Each malformed frame ends the connection with a protocol violation and a fixed message: the callback never sees
   /// it, the socket is aborted, and a send that was waiting for its acknowledgement fails so its caller replays the
   /// durable record on the next connection. That send is told the channel closed, with the violation as the cause, and
   /// a send attempted afterwards is told the same without one.
   /// </summary>
   [Theory]
   [InlineData("not-json", "The realtime frame is not valid JSON.")]
   [InlineData("empty", "The realtime frame is not valid JSON.")]
   [InlineData("too-deep", "The realtime frame is not valid JSON.")]
   [InlineData("array", "The realtime frame is not a JSON object.")]
   [InlineData("null", "The realtime frame is not a JSON object.")]
   [InlineData("repeated-field", "The realtime frame repeats a field.")]
   [InlineData("repeated-nested-field", "The realtime frame repeats a field.")]
   [InlineData("repeated-field-in-list", "The realtime frame repeats a field.")]
   [InlineData("type-missing", "The realtime frame has no usable type, message identifier or payload object.")]
   [InlineData("type-empty", "The realtime frame has no usable type, message identifier or payload object.")]
   [InlineData("type-long", "The realtime frame has no usable type, message identifier or payload object.")]
   [InlineData("type-number", "The realtime frame does not have the expected fields.")]
   [InlineData("id-missing", "The realtime frame has no usable type, message identifier or payload object.")]
   [InlineData("id-empty", "The realtime frame has no usable type, message identifier or payload object.")]
   [InlineData("id-long", "The realtime frame has no usable type, message identifier or payload object.")]
   [InlineData("id-object", "The realtime frame does not have the expected fields.")]
   [InlineData("payload-missing", "The realtime frame has no usable type, message identifier or payload object.")]
   [InlineData("payload-null", "The realtime frame has no usable type, message identifier or payload object.")]
   [InlineData("payload-list", "The realtime frame has no usable type, message identifier or payload object.")]
   [InlineData("payload-text", "The realtime frame has no usable type, message identifier or payload object.")]
   [InlineData("ack-without-id", "The realtime ACK frame has no usable acknowledgedMessageId.")]
   [InlineData("ack-number-id", "The realtime ACK frame has no usable acknowledgedMessageId.")]
   [InlineData("ack-empty-id", "The realtime ACK frame has no usable acknowledgedMessageId.")]
   [InlineData("ack-long-id", "The realtime ACK frame has no usable acknowledgedMessageId.")]
   [InlineData("error-without-code", "The realtime ERROR frame has no usable code.")]
   [InlineData("error-number-code", "The realtime ERROR frame has no usable code.")]
   [InlineData("error-long-code", "The realtime ERROR frame has no usable code.")]
   [InlineData("binary", "The realtime message is not text or exceeds the negotiated frame size.")]
   [InlineData("oversized", "The realtime message is not text or exceeds the negotiated frame size.")]
   [InlineData("oversized-in-fragments", "The realtime message is not text or exceeds the negotiated frame size.")]
   public async Task EachMalformedFrameIsAProtocolViolationThatEndsTheConnection(string fault, string message)
   {
      using var socket = new ScriptedWebSocket();
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings(MaxFrameBytes: 8192, MaxQueuedFrameBytes: 8192));
      var received = new List<RealtimeEnvelope>();
      Task run = channel.RunAsync(received.Add, CancellationToken.None);
      Task waiting = channel.SendAsync("JOB_EVENT", new { jobId = "job-1" }, "event-1", acknowledged: true);
      await socket.NextSentAsync();
      Deliver(socket, fault);
      PrintingProtocolException violation = await Assert.ThrowsAsync<PrintingProtocolException>(() => run.WaitAsync(Patience));
      Assert.Equal(message, violation.Message);
      Assert.Empty(received);
      Assert.Equal(1, socket.Aborts);
      Assert.False(channel.IsOpen);
      RealtimeChannelClosedException released = await Assert.ThrowsAsync<RealtimeChannelClosedException>(() => waiting.WaitAsync(Patience));
      Assert.Equal("Realtime connection closed before acknowledgement; durable receipts will be replayed.", released.Message);
      Assert.Same(violation, released.InnerException);
      Assert.Null((await Assert.ThrowsAsync<RealtimeChannelClosedException>(() => channel.SendAsync("HEARTBEAT", new { }))).InnerException);
   }



   /// <summary>The server closing the socket, or the connection dropping, is a lost connection, not a protocol violation, so the two can be told apart; both still release a waiting send.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task CloseOrDropIsALostConnectionNotAViolation(bool closeFrame)
   {
      using var socket = new ScriptedWebSocket();
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      Task run = channel.RunAsync(_ => { }, CancellationToken.None);
      Task waiting = channel.SendAsync("JOB_EVENT", new { jobId = "job-1" }, "event-1", acknowledged: true);
      await socket.NextSentAsync();
      if(closeFrame)
      { socket.DeliverClose(); }
      else
      { socket.Drop(); }
      Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(Patience));
      Assert.IsNotType<PrintingProtocolException>(failure);
      Assert.Equal(closeFrame ? typeof(IOException) : typeof(WebSocketException), failure.GetType());
      Assert.Same(failure, (await Assert.ThrowsAsync<RealtimeChannelClosedException>(() => waiting.WaitAsync(Patience))).InnerException);
   }



   /// <summary>
   /// A message split across frames is assembled and delivered once, with its size on the wire; its payload stays readable
   /// after later frames arrive; and the download meter counts exactly the bytes received.
   /// </summary>
   [Fact]
   public async Task FragmentedMessageIsAssembledAndItsPayloadOutlivesTheReadBuffer()
   {
      var meter = new ApplicationTrafficMeter();
      using var socket = new ScriptedWebSocket();
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings(), meter);
      var received = System.Threading.Channels.Channel.CreateUnbounded<RealtimeEnvelope>();
      using var stop = new CancellationTokenSource();
      Task run = channel.RunAsync(envelope => Assert.True(received.Writer.TryWrite(envelope)), stop.Token);
      byte[] first = Encoding.UTF8.GetBytes("""{"type":"DESIRED_STATE","messageId":"state-1","payload":{"revision":7,"dispatchPaused":true,"connectionState":"CONNECTED"}}""");
      socket.DeliverFrame(first[..20], WebSocketMessageType.Text, end: false);
      socket.DeliverFrame(first[20..], WebSocketMessageType.Text, end: true);
      socket.Deliver("JOB_AVAILABLE", "wake-1", new { });
      RealtimeEnvelope state = await received.Reader.ReadAsync(stop.Token).AsTask().WaitAsync(Patience);
      RealtimeEnvelope wake = await received.Reader.ReadAsync(stop.Token).AsTask().WaitAsync(Patience);
      Assert.Equal("DESIRED_STATE", state.Type);
      Assert.Equal("state-1", state.MessageId);
      Assert.Equal(first.Length, state.WireLength);
      Assert.Equal(new DeviceDesiredState(7, true, "CONNECTED"), state.ReadPayload<DeviceDesiredState>());
      Assert.Equal("JOB_AVAILABLE", wake.Type);
      Assert.Equal(first.Length + wake.WireLength, meter.GetSnapshot().DownloadedBytes);
      await stop.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
   }



   /// <summary>An ACK completes exactly the send it names and is not shown to the callback; an ACK for a message nobody is waiting on (a late one, after a timeout) is ignored without ending the connection.</summary>
   [Fact]
   public async Task AckCompletesOnlyTheSendItNames()
   {
      var meter = new ApplicationTrafficMeter();
      using var socket = new ScriptedWebSocket();
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings(), meter);
      var received = new List<RealtimeEnvelope>();
      using var stop = new CancellationTokenSource();
      Task run = channel.RunAsync(received.Add, stop.Token);
      Task first = channel.SendAsync("JOB_EVENT", new { jobId = "job-1" }, "event-1", acknowledged: true);
      Task second = channel.SendAsync("JOB_EVENT", new { jobId = "job-2" }, "event-2", acknowledged: true);
      JsonElement sent = await socket.NextSentAsync();
      await socket.NextSentAsync();
      Assert.Equal("JOB_EVENT", sent.GetProperty("type").GetString());
      Assert.Equal("job-1", sent.GetProperty("payload").GetProperty("jobId").GetString());
      socket.Deliver("ACK", "ack-0", new { acknowledgedMessageId = "event-from-an-earlier-connection" });
      socket.Deliver("ACK", "ack-2", new { acknowledgedMessageId = "event-2" });
      await second.WaitAsync(Patience);
      Assert.False(first.IsCompleted);
      socket.Deliver("ACK", "ack-1", new { acknowledgedMessageId = "event-1" });
      await first.WaitAsync(Patience);
      Assert.Empty(received);
      Assert.True(channel.IsOpen);
      Assert.True(meter.GetSnapshot().UploadedBytes > 0);
      await stop.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
   }



   /// <summary>
   /// An ERROR for a send awaiting acknowledgement fails that send with the HTTP status the same refusal has over HTTP and
   /// is not passed on, except when it says authorization is gone: then the send fails and the runtime is told too.
   /// </summary>
   [Theory]
   [InlineData("NOT_FOUND", HttpStatusCode.NotFound, false)]
   [InlineData("EXPIRED", HttpStatusCode.Gone, false)]
   [InlineData("CONFLICT", HttpStatusCode.Conflict, false)]
   [InlineData("PERMISSION_DENIED", HttpStatusCode.BadRequest, false)]
   [InlineData("AUTHORIZATION_REQUIRED", HttpStatusCode.BadRequest, true)]
   [InlineData("AUTHENTICATION_REQUIRED", HttpStatusCode.BadRequest, true)]
   public async Task ErrorFailsTheWaitingSendWithTheMatchingStatus(string code, HttpStatusCode status, bool forwarded)
   {
      using var socket = new ScriptedWebSocket();
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      var received = System.Threading.Channels.Channel.CreateUnbounded<RealtimeEnvelope>();
      using var stop = new CancellationTokenSource();
      Task run = channel.RunAsync(envelope => Assert.True(received.Writer.TryWrite(envelope)), stop.Token);
      Task send = channel.SendAsync("COMMAND_RECEIPT", new { commandId = "command-1" }, "receipt-1", acknowledged: true);
      await socket.NextSentAsync();
      socket.Deliver("ERROR", "receipt-1", new { code, message = "Synthetic refusal.", retryable = false });
      socket.Deliver("PONG", "pong-1", new { serverTime = "2026-01-01T00:00:00Z" });
      HttpRequestException rejection = await Assert.ThrowsAsync<HttpRequestException>(() => send.WaitAsync(Patience));
      Assert.Equal(status, rejection.StatusCode);
      Assert.Equal("Realtime operation was rejected: " + code, rejection.Message);
      RealtimeEnvelope next = await received.Reader.ReadAsync(stop.Token).AsTask().WaitAsync(Patience);
      Assert.Equal(forwarded ? "ERROR" : "PONG", next.Type);
      await stop.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
   }



   /// <summary>An ERROR that answers no waiting send is handed to the runtime as an ordinary message, so a server-side refusal that arrives late is still seen.</summary>
   [Fact]
   public async Task ErrorForNoWaitingSendIsPassedToTheRuntime()
   {
      using var socket = new ScriptedWebSocket();
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      var received = System.Threading.Channels.Channel.CreateUnbounded<RealtimeEnvelope>();
      using var stop = new CancellationTokenSource();
      Task run = channel.RunAsync(envelope => Assert.True(received.Writer.TryWrite(envelope)), stop.Token);
      socket.Deliver(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "realtime-error.message.json")));
      RealtimeEnvelope error = await received.Reader.ReadAsync(stop.Token).AsTask().WaitAsync(Patience);
      Assert.Equal("ERROR", error.Type);
      Assert.Equal("PERMISSION_DENIED", error.Payload.GetProperty("code").GetString());
      await stop.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
   }



   /// <summary>The shared realtime command fixture passes validation, and every way a command can be unsafe to act on is a protocol violation, the same failure a malformed frame raises.</summary>
   [Theory]
   [InlineData("valid", true)]
   [InlineData("commandId-path", false)]
   [InlineData("commandId-missing", false)]
   [InlineData("requestId-long", false)]
   [InlineData("action-missing", false)]
   [InlineData("action-long", false)]
   [InlineData("created-not-a-time", false)]
   [InlineData("expires-not-a-time", false)]
   [InlineData("expires-before-created", false)]
   [InlineData("lifetime-over-fifteen-minutes", false)]
   [InlineData("cancel-without-job", false)]
   [InlineData("cancel-with-unsafe-job", false)]
   public async Task RealtimeCommandIsValidatedAsAProtocolBoundary(string fault, bool valid)
   {
      var envelope = RealtimeEnvelope.Parse(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "realtime-command.message.json")));
      DeviceControlCommand command = envelope.ReadPayload<DeviceControlCommand>();
      command = fault switch
      {
         "commandId-path" => command with { CommandId = "../command" },
         "commandId-missing" => command with { CommandId = null! },
         "requestId-long" => command with { RequestId = new string('r', 201) },
         "action-missing" => command with { Action = "" },
         "action-long" => command with { Action = new string('A', 65) },
         "created-not-a-time" => command with { CreatedAt = "yesterday" },
         "expires-not-a-time" => command with { ExpiresAt = "" },
         "expires-before-created" => command with { ExpiresAt = "2026-10-31T21:59:59Z" },
         "lifetime-over-fifteen-minutes" => command with { ExpiresAt = "2026-10-31T22:15:01Z" },
         "cancel-without-job" => command with { JobId = null },
         "cancel-with-unsafe-job" => command with { JobId = "job/../1" },
         _ => command,
      };
      if(valid)
      {
         PrintApiClient.ValidateRealtimeCommand(command);
         Assert.Equal("CANCEL_SPOOL", command.Action);
         Assert.Equal("job-example-1", command.JobId);
      }
      else
      {
         PrintingProtocolException violation = Assert.Throws<PrintingProtocolException>(() => PrintApiClient.ValidateRealtimeCommand(command));
         Assert.Equal("The realtime command frame carries an invalid device control command.", violation.Message);
      }
   }



   /// <summary>A missing command, as a frame without a payload would produce, is refused like any other invalid command.</summary>
   [Fact]
   public void MissingRealtimeCommandIsAProtocolViolation()
      => Assert.Throws<PrintingProtocolException>(() => PrintApiClient.ValidateRealtimeCommand(null!));



   /// <summary>Reading a payload as a contract type reports a member of the wrong JSON type, or a payload that is not an object, as a protocol violation rather than a JSON error.</summary>
   [Fact]
   public void PayloadOfTheWrongShapeIsAProtocolViolation()
   {
      var wrongType = RealtimeEnvelope.Parse(Encoding.UTF8.GetBytes("""{"type":"DESIRED_STATE","messageId":"state-1","payload":{"revision":"seven","dispatchPaused":true,"connectionState":"CONNECTED"}}"""));
      Assert.Equal("The realtime payload does not have the expected fields.", Assert.Throws<PrintingProtocolException>(() => wrongType.ReadPayload<DeviceDesiredState>()).Message);
      var incomplete = RealtimeEnvelope.Parse(Encoding.UTF8.GetBytes("""{"type":"DESIRED_STATE","messageId":"state-1","payload":{"revision":7}}"""));
      Assert.Throws<PrintingProtocolException>(() => incomplete.ReadPayload<DeviceDesiredState>());
      using var list = JsonDocument.Parse("[]");
      var handBuilt = new RealtimeEnvelope("DESIRED_STATE", "state-1", list.RootElement);
      Assert.Equal("The realtime payload is not a JSON object.", Assert.Throws<PrintingProtocolException>(() => handBuilt.ReadPayload<DeviceDesiredState>()).Message);
   }



   /// <summary>Queues the frame, or frames, that make up one malformed shape.</summary>
   private static void Deliver(ScriptedWebSocket socket, string fault)
   {
      string? json = fault switch
      {
         "not-json" => "<html>proxy error</html>",
         "empty" => "",
         "too-deep" => string.Concat(Enumerable.Repeat("{\"a\":", 25)) + "1" + new string('}', 25),
         "array" => "[]",
         "null" => "null",
         "repeated-field" => """{"type":"PING","TYPE":"ACK","messageId":"m-1","payload":{}}""",
         "repeated-nested-field" => """{"type":"PING","messageId":"m-1","payload":{"a":1,"A":2}}""",
         "repeated-field-in-list" => """{"type":"PING","messageId":"m-1","payload":{"list":[{"a":1,"a":2}]}}""",
         "type-missing" => """{"messageId":"m-1","payload":{}}""",
         "type-empty" => """{"type":"","messageId":"m-1","payload":{}}""",
         "type-long" => "{\"type\":\"" + new string('T', 65) + "\",\"messageId\":\"m-1\",\"payload\":{}}",
         "type-number" => """{"type":7,"messageId":"m-1","payload":{}}""",
         "id-missing" => """{"type":"PING","payload":{}}""",
         "id-empty" => """{"type":"PING","messageId":"","payload":{}}""",
         "id-long" => "{\"type\":\"PING\",\"messageId\":\"" + new string('m', 201) + "\",\"payload\":{}}",
         "id-object" => """{"type":"PING","messageId":{},"payload":{}}""",
         "payload-missing" => """{"type":"PING","messageId":"m-1"}""",
         "payload-null" => """{"type":"PING","messageId":"m-1","payload":null}""",
         "payload-list" => """{"type":"PING","messageId":"m-1","payload":[]}""",
         "payload-text" => """{"type":"PING","messageId":"m-1","payload":"{}"}""",
         "ack-without-id" => """{"type":"ACK","messageId":"m-1","payload":{}}""",
         "ack-number-id" => """{"type":"ACK","messageId":"m-1","payload":{"acknowledgedMessageId":1}}""",
         "ack-empty-id" => """{"type":"ACK","messageId":"m-1","payload":{"acknowledgedMessageId":""}}""",
         "ack-long-id" => "{\"type\":\"ACK\",\"messageId\":\"m-1\",\"payload\":{\"acknowledgedMessageId\":\"" + new string('a', 201) + "\"}}",
         "error-without-code" => """{"type":"ERROR","messageId":"event-1","payload":{"message":"refused"}}""",
         "error-number-code" => """{"type":"ERROR","messageId":"unrelated","payload":{"code":404}}""",
         "error-long-code" => "{\"type\":\"ERROR\",\"messageId\":\"event-1\",\"payload\":{\"code\":\"" + new string('C', 129) + "\"}}",
         _ => null,
      };
      if(json is not null)
      {
         socket.Deliver(json);
         return;
      }
      switch(fault)
      {
         case "binary":
            socket.DeliverFrame([1, 2, 3], WebSocketMessageType.Binary, end: true);
            break;
         case "oversized":
            socket.DeliverFrame(new byte[8192], WebSocketMessageType.Text, end: false);
            socket.DeliverFrame([(byte)' '], WebSocketMessageType.Text, end: true);
            break;
         default:
            for(int fragment = 0; fragment < 9; fragment++)
            { socket.DeliverFrame(new byte[1024], WebSocketMessageType.Text, end: false); }
            break;
      }
   }
}
