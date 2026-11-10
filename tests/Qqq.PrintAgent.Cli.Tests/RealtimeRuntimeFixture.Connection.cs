/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.WebSockets;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Testing;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>The realtime peer's side of the fixture: one accepted WebSocket and the pump that answers the agent on it.</summary>
internal sealed partial class RealtimeRuntimeFixture
{
   /// <summary>
   /// The server side of one accepted WebSocket. A background pump records every agent message, acknowledges it (the server's
   /// durable-receipt promise), answers pings, releases claimed jobs on RECEIVED, and can refuse a message with an ERROR frame or
   /// drop the connection at a chosen message to simulate losing an acknowledgement.
   /// </summary>
   internal sealed class Connection(WebSocket socket, RealtimeRuntimeFixture fixture, bool loseSpoolAck, CancellationToken token) : IAsyncDisposable
   {
      private readonly SemaphoreSlim _send = new(1, 1);
      private Task _pump = Task.CompletedTask;
      private DeviceDesiredState? _newestDesired;
      internal Task Completion => _pump;



      /// <summary>Drops the socket without a close handshake, as a network failure would.</summary>
      internal void Abort() => socket.Abort();



      /// <summary>Begins reading and acknowledging agent messages.</summary>
      internal void Start() => _pump = PumpAsync();



      /// <summary>Completed when the connection deliberately drops the SPOOLED receipt without acknowledging it, so a test can wait for that exact moment before reconnecting.</summary>
      internal TaskCompletionSource LostSpoolAck { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);



      /// <summary>Completed when the connection dropped a message the fixture was told to lose, so a test can wait for that exact moment before reconnecting.</summary>
      internal TaskCompletionSource LostAcknowledgement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);



      /// <summary>Sends one server message, serialized so concurrent senders cannot interleave frames, and remembers the newest desired state sent so it can be sent again if the agent does not apply it.</summary>
      internal async Task SendAsync(string type, object payload)
      {
         if(payload is DeviceDesiredState desired && type is "DESIRED_STATE")
         { _newestDesired = desired; }
         await _send.WaitAsync(token);
         try
         { await LoopbackWebSocketPeer.SendAsync(socket, type, payload, token); }
         finally { _send.Release(); }
      }



      /// <summary>Answers one agent message with an ERROR frame that carries the same message id, which is how the server refuses an operation that was waiting for an acknowledgement.</summary>
      private async Task SendErrorAsync(string messageId, string code)
      {
         await _send.WaitAsync(token);
         try
         { await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "ERROR", messageId, payload = new { code } }, PrintApiClient.JsonOptions), WebSocketMessageType.Text, true, token); }
         finally { _send.Release(); }
      }



      /// <summary>
      /// Reads agent messages until cancelled. Test hooks can hold acknowledgement (before-ack, state-applied, received-release) to
      /// create precise timing windows. A RECEIVED event frees the server's claimed job; PING gets PONG; otherwise ACK echoes the
      /// message id, unless the test asked for the message to be lost (the connection is dropped unacknowledged) or refused.
      /// </summary>
      private async Task PumpAsync()
      {
         while(!token.IsCancellationRequested)
         {
            RealtimeEnvelope message = await LoopbackWebSocketPeer.ReceiveAsync(socket, token);
            fixture.Messages.Enqueue(message);
            if(fixture.BeforeAcknowledgement is not null)
            { await fixture.BeforeAcknowledgement(message); }
            if(message.Type is "STATE_APPLIED" && fixture.StateAppliedRelease is not null)
            { await fixture.StateAppliedRelease.Task.WaitAsync(token); }
            if(message.Type is "JOB_EVENT" && message.Payload.GetProperty("event").GetProperty("status").GetString() is "RECEIVED")
            { await ReleaseClaimAsync(message); }
            if(message.Type is "PING")
            { await SendAsync("PONG", new { serverTime = Now() }); continue; }
            if(loseSpoolAck && message.Type is "JOB_EVENT" && message.Payload.GetProperty("event").GetProperty("status").GetString() is "SPOOLED")
            {
               LostSpoolAck.TrySetResult();
               socket.Abort();
               return;
            }
            if(fixture.LoseAcknowledgementOf?.Invoke(message) == true)
            {
               LostAcknowledgement.TrySetResult();
               socket.Abort();
               return;
            }
            if(fixture.RefuseWith?.Invoke(message) is string code)
            {
               await SendErrorAsync(message.MessageId, code);
               if(!fixture.CloseAfterRefusal)
               { continue; }
               await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "refused", token);
               return;
            }
            await SendAsync("ACK", new { acknowledgedMessageId = message.MessageId });
            if(message.Type is "STATE_APPLIED" && fixture.ResendUnappliedState && _newestDesired is DeviceDesiredState unapplied
               && message.Payload.GetProperty("lastAppliedRevision").GetInt64() < unapplied.Revision)
            { await SendAsync("DESIRED_STATE", unapplied); }
         }
      }



      /// <summary>Frees the server's claimed job once its RECEIVED event arrived, after any hold the test placed on that event.</summary>
      private async Task ReleaseClaimAsync(RealtimeEnvelope message)
      {
         if(fixture.ReceivedRelease is not null)
         { await fixture.ReceivedRelease.Task.WaitAsync(token); }
         lock(fixture._claimLock)
         {
            if(string.Equals(fixture._claimed?.JobId, message.Payload.GetProperty("jobId").GetString(), StringComparison.Ordinal))
            { fixture._claimed = null; }
         }
      }



      /// <summary>Aborts the socket, lets the pump finish and releases the socket and send gate.</summary>
      public async ValueTask DisposeAsync()
      {
         socket.Abort();
         try
         { await _pump; }
         catch(Exception exception) when(exception is OperationCanceledException or WebSocketException or IOException) { }
         socket.Dispose();
         _send.Dispose();
      }
   }
}
