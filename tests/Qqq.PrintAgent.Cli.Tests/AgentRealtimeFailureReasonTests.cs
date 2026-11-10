/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.WebSockets;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Testing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Which failure is reported as the reason a realtime connection ended. Several of a connection's tasks usually fail together,
/// and which of them completes first is a matter of scheduling, so the choice is made by rule. Most tests here complete tasks
/// by hand, in the order that would mislead; one runs a real channel to show that a send it fails carries the cause.
/// </summary>
public sealed class AgentRealtimeFailureReasonTests
{
   /// <summary>
   /// The server refuses a message and then closes the socket. The reader's task completes first here, with the closure, and
   /// the worker's afterwards, with the refusal. The refusal is the reason; the closure is passed on as detail.
   /// </summary>
   [Fact]
   public async Task WorkerFailureWithItsOwnReasonComesBeforeTheReadersEvenWhenTheReaderFailedFirst()
   {
      var reader = new TaskCompletionSource();
      var worker = new TaskCompletionSource();
      var idle = new TaskCompletionSource();
      List<Exception> detail = [];
      Task completion = AgentRuntime.CompleteConnectionAsync(detail.Add, reader.Task, worker.Task, idle.Task);
      reader.SetException(new IOException("Server closed the realtime connection."));
      idle.SetCanceled();
      Assert.False(completion.IsCompleted);
      worker.SetException(new HttpRequestException("Realtime operation was rejected: CONFLICT"));
      HttpRequestException reason = await Assert.ThrowsAsync<HttpRequestException>(() => completion);
      Assert.Equal("Realtime operation was rejected: CONFLICT", reason.Message);
      Assert.Equal("Server closed the realtime connection.", Assert.Single(detail).Message);
   }



   /// <summary>
   /// The reader rejects a frame and the channel fails a send that was waiting for an acknowledgement. The send's task
   /// completes first, but its plain I/O error only says the connection was gone. The reader's failure is the reason, and the
   /// failed send, which carries that failure as its cause, is detail.
   /// </summary>
   [Fact]
   public async Task ReaderFailureIsTheReasonWhenAWorkerOnlyFoundTheConnectionGone()
   {
      var frame = new InvalidDataException("The PRINTERS frame has no printers list.");
      var reader = new TaskCompletionSource();
      var sender = new TaskCompletionSource();
      List<Exception> detail = [];
      Task completion = AgentRuntime.CompleteConnectionAsync(detail.Add, reader.Task, sender.Task);
      sender.SetException(new IOException("Realtime connection closed before acknowledgement; durable receipts will be replayed.", frame));
      Assert.False(completion.IsCompleted);
      reader.SetException(frame);
      Assert.Same(frame, await Assert.ThrowsAsync<InvalidDataException>(() => completion));
      Assert.Same(frame, Assert.Single(detail).InnerException);
   }



   /// <summary>A session the server ended is an I/O error by type but a reason of its own, so it comes before the reader's failure like any other worker failure that is not a plain I/O error.</summary>
   [Fact]
   public async Task SessionFailureFromAWorkerComesBeforeTheReaders()
   {
      var reader = new TaskCompletionSource();
      var worker = new TaskCompletionSource();
      List<Exception> detail = [];
      Task completion = AgentRuntime.CompleteConnectionAsync(detail.Add, reader.Task, worker.Task);
      reader.SetException(new IOException("Server closed the realtime connection."));
      worker.SetException(new Qqq.PrintAgent.Core.Authentication.QqqSessionException(Qqq.PrintAgent.Core.Authentication.QqqSessionException.FailureKind.AuthenticationRequired, "Synthetic session ended."));
      _ = await Assert.ThrowsAsync<Qqq.PrintAgent.Core.Authentication.QqqSessionException>(() => completion);
      _ = Assert.Single(detail);
   }



   /// <summary>When another task ends the connection, the reader is only cancelled by the teardown, so that task's failure is the reason, whatever its kind.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task WorkerFailureIsTheReasonWhenTheReaderWasOnlyCancelled(bool plainIoError)
   {
      var reader = new TaskCompletionSource();
      var worker = new TaskCompletionSource();
      List<Exception> detail = [];
      Task completion = AgentRuntime.CompleteConnectionAsync(detail.Add, reader.Task, worker.Task);
      Exception failure = plainIoError ? new IOException("Realtime output queue is full; reconnect is required.") : new TimeoutException("Synthetic acknowledgement timeout.");
      worker.SetException(failure);
      reader.SetCanceled();
      Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => completion));
      Assert.Empty(detail);
   }



   /// <summary>A connection that was only cancelled (shutdown, sign-in, an explicit reconnect) reports cancellation, not a failure.</summary>
   [Fact]
   public async Task CancellationIsReportedWhenNothingFailed()
   {
      var reader = new TaskCompletionSource();
      var worker = new TaskCompletionSource();
      List<Exception> detail = [];
      Task completion = AgentRuntime.CompleteConnectionAsync(detail.Add, reader.Task, worker.Task);
      reader.SetCanceled();
      worker.SetCanceled();
      _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
      Assert.Empty(detail);
   }



   /// <summary>
   /// A real channel on a loopback socket: a send is waiting for its acknowledgement when the reader rejects a frame. The
   /// channel fails the send, and that failure carries the reader's as its cause, so the consequence can always be traced to
   /// what ended the connection.
   /// </summary>
   [Fact]
   public async Task SendTheChannelFailsCarriesWhatEndedTheChannelAsItsCause()
   {
      using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
      using var peer = new LoopbackWebSocketPeer();
      using var client = new ClientWebSocket();
      client.Options.AddSubProtocol("qqq-printing.v1");
      Task<WebSocket> accepting = peer.AcceptAsync(guard.Token);
      await client.ConnectAsync(new Uri("ws://127.0.0.1:" + peer.Origin.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/qqq/v1/qbit/printing/device/realtime"), guard.Token);
      using WebSocket server = await accepting;
      using var channel = new PrintingRealtimeChannel(client, new RealtimeSettings());
      var rejected = new InvalidDataException("Synthetic rejected frame.");
      Task running = channel.RunAsync(_ => throw rejected, guard.Token);
      Task pending = channel.SendAsync("INVENTORY", new { printers = Array.Empty<object>() }, acknowledged: true, token: guard.Token);
      RealtimeEnvelope sent = await LoopbackWebSocketPeer.ReceiveAsync(server, guard.Token);
      Assert.Equal("INVENTORY", sent.Type);
      await LoopbackWebSocketPeer.SendAsync(server, "PRINTERS", new { nothing = true }, guard.Token);
      IOException failed = await Assert.ThrowsAsync<IOException>(() => pending);
      Assert.Contains("closed before acknowledgement", failed.Message, StringComparison.Ordinal);
      Assert.Same(rejected, failed.InnerException);
      Assert.Same(rejected, await Assert.ThrowsAsync<InvalidDataException>(() => running));
   }
}
