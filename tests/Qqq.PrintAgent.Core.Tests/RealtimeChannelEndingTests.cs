/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.WebSockets;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Testing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// How a run of the realtime channel ends says why the channel ended, and the service decides what to log and whether to
/// reconnect from that. A run that was ended on purpose (its caller cancelled, or the channel was disposed) returns; a
/// run that ended by itself throws what ended it. Every ending also releases a send that was waiting for its
/// acknowledgement, telling it the channel closed and, when something failed, what failed.
/// </summary>
public sealed class RealtimeChannelEndingTests
{
   private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);



   /// <summary>
   /// The caller cancels the token it gave the run: the run returns without a failure, because nothing failed. The
   /// socket is aborted, the channel is closed, and a send that was waiting is released with no cause to carry.
   /// </summary>
   [Fact]
   public async Task CallerCancellationEndsTheRunNormally()
   {
      using var socket = new ScriptedWebSocket();
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      using var stop = new CancellationTokenSource();
      Task run = channel.RunAsync(_ => { }, stop.Token);
      Task waiting = channel.SendAsync("JOB_EVENT", new { jobId = "job-1" }, "event-1", acknowledged: true);
      await socket.NextSentAsync();
      await stop.CancelAsync();
      await run.WaitAsync(Patience);
      Assert.True(run.IsCompletedSuccessfully);
      Assert.Equal(1, socket.Aborts);
      Assert.False(channel.IsOpen);
      RealtimeChannelClosedException released = await Assert.ThrowsAsync<RealtimeChannelClosedException>(() => waiting.WaitAsync(Patience));
      Assert.Equal("Realtime connection closed before acknowledgement; durable receipts will be replayed.", released.Message);
      Assert.Null(released.InnerException);
      Assert.Null((await Assert.ThrowsAsync<RealtimeChannelClosedException>(() => channel.SendAsync("HEARTBEAT", new { }))).InnerException);
   }



   /// <summary>The server closing the socket is the channel ending by itself: the run throws the closure, and the waiting send carries it as its cause.</summary>
   [Fact]
   public async Task ServerCloseEndsTheRunWithTheClosure()
   {
      using var socket = new ScriptedWebSocket();
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      Task run = channel.RunAsync(_ => { }, CancellationToken.None);
      Task waiting = channel.SendAsync("JOB_EVENT", new { jobId = "job-1" }, "event-1", acknowledged: true);
      await socket.NextSentAsync();
      socket.DeliverClose();
      IOException closure = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(Patience));
      Assert.Equal("Server closed the realtime connection.", closure.Message);
      Assert.Same(closure, (await Assert.ThrowsAsync<RealtimeChannelClosedException>(() => waiting.WaitAsync(Patience))).InnerException);
   }



   /// <summary>A reader that fails, here because the callback threw, ends the run with that very failure, and the waiting send carries it as its cause.</summary>
   [Fact]
   public async Task ReaderFailureEndsTheRunWithThatFailure()
   {
      var rejected = new InvalidDataException("Synthetic rejected frame.");
      using var socket = new ScriptedWebSocket();
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      Task run = channel.RunAsync(_ => throw rejected, CancellationToken.None);
      Task waiting = channel.SendAsync("JOB_EVENT", new { jobId = "job-1" }, "event-1", acknowledged: true);
      await socket.NextSentAsync();
      socket.Deliver("JOB_AVAILABLE", "wake-1", new { });
      Assert.Same(rejected, await Assert.ThrowsAsync<InvalidDataException>(() => run.WaitAsync(Patience)));
      Assert.Same(rejected, (await Assert.ThrowsAsync<RealtimeChannelClosedException>(() => waiting.WaitAsync(Patience))).InnerException);
      Assert.Equal(1, socket.Aborts);
   }



   /// <summary>
   /// A writer that fails, because the socket refused a frame, ends the run with that failure although the reader was
   /// only cancelled by the teardown, and the send whose frame it was carries the failure as its cause.
   /// </summary>
   [Fact]
   public async Task WriterFailureEndsTheRunWithThatFailure()
   {
      var refused = new IOException("Synthetic send failure.");
      using var socket = new ScriptedWebSocket { Sending = _ => throw refused };
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      Task run = channel.RunAsync(_ => { }, CancellationToken.None);
      Task waiting = channel.SendAsync("JOB_EVENT", new { jobId = "job-1" }, "event-1", acknowledged: true);
      Assert.Same(refused, await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(Patience)));
      Assert.Same(refused, (await Assert.ThrowsAsync<RealtimeChannelClosedException>(() => waiting.WaitAsync(Patience))).InnerException);
      Assert.False(channel.IsOpen);
   }



   /// <summary>
   /// Disposing the channel while it runs is its owner ending it on purpose: the run returns, although the caller's
   /// token was never cancelled, and it does not report the failure the reader met on the socket that was disposed
   /// under it. The waiting send is told the channel closed.
   /// </summary>
   [Fact]
   public async Task DisposeWhileRunningEndsTheRunNormally()
   {
      using var socket = new ScriptedWebSocket();
      var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      using var never = new CancellationTokenSource();
      Task run = channel.RunAsync(_ => { }, never.Token);
      Task waiting = channel.SendAsync("JOB_EVENT", new { jobId = "job-1" }, "event-1", acknowledged: true);
      await socket.NextSentAsync();
      channel.Dispose();
      await run.WaitAsync(Patience);
      Assert.True(run.IsCompletedSuccessfully);
      Assert.False(never.IsCancellationRequested);
      Assert.True(socket.Disposed);
      RealtimeChannelClosedException released = await Assert.ThrowsAsync<RealtimeChannelClosedException>(() => waiting.WaitAsync(Patience));
      Assert.Equal("Realtime connection was disposed.", released.Message);
      Assert.Null(released.InnerException);
   }



   /// <summary>
   /// The two endings on purpose, on the platform's own client socket and a loopback peer instead of the scripted
   /// socket: what a real socket throws when its receive is cancelled, or when it is disposed under a waiting receive,
   /// must not turn an orderly ending into a failure either.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task EndingOnPurposeIsNormalOnAPlatformSocketToo(bool dispose)
   {
      using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
      using var stop = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
      using var peer = new LoopbackWebSocketPeer();
      using var client = new ClientWebSocket();
      client.Options.AddSubProtocol("qqq-printing.v1");
      Task<WebSocket> accepting = peer.AcceptAsync(guard.Token);
      await client.ConnectAsync(new Uri($"ws://127.0.0.1:{peer.Origin.Port}/qqq/v1/qbit/printing/device/realtime"), guard.Token);
      using WebSocket server = await accepting;
      var channel = new PrintingRealtimeChannel(client, new RealtimeSettings());
      Task run = channel.RunAsync(_ => { }, stop.Token);
      await channel.SendAsync("HEARTBEAT", new { }, token: guard.Token);
      Assert.Equal("HEARTBEAT", (await LoopbackWebSocketPeer.ReceiveAsync(server, guard.Token)).Type);
      if(dispose)
      { channel.Dispose(); }
      else
      { await stop.CancelAsync(); }
      await run.WaitAsync(Patience);
      Assert.True(run.IsCompletedSuccessfully);
      Assert.False(guard.IsCancellationRequested);
      channel.Dispose();
   }



   /// <summary>
   /// A callback may give up by throwing a cancellation of its own, as the service's does when a newer connection has
   /// replaced this one. The caller's token was not cancelled, so the run does not pretend the caller asked: it ends as
   /// that cancellation.
   /// </summary>
   [Fact]
   public async Task CallbackThatGivesUpEndsTheRunAsACancellation()
   {
      using var socket = new ScriptedWebSocket();
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      using var never = new CancellationTokenSource();
      Task run = channel.RunAsync(_ => throw new OperationCanceledException("Replaced by a newer connection."), never.Token);
      socket.Deliver("JOB_AVAILABLE", "wake-1", new { });
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Patience));
      Assert.True(run.IsCanceled);
      Assert.False(never.IsCancellationRequested);
   }



   /// <summary>
   /// The caller's cancellation can overtake the reader: the reader has checked the token and is about to ask the socket
   /// for a frame when the caller cancels and the shutdown aborts the socket, so the reader fails on the aborted socket
   /// instead of seeing the cancellation. Nothing failed, so the run still returns. The hook holds the reader at exactly
   /// that point until the socket has been aborted.
   /// </summary>
   [Fact]
   public async Task CallerCancellationThatOvertakesTheReaderStillEndsTheRunNormally()
   {
      using var stop = new CancellationTokenSource();
      using var socket = new ScriptedWebSocket();
      int receives = 0;
      socket.Receiving = () =>
      {
         if(Interlocked.Increment(ref receives) == 2)
         {
            stop.Cancel();
            Assert.True(SpinWait.SpinUntil(() => socket.Aborts > 0, Patience));
         }
      };
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      Task run = channel.RunAsync(_ => { }, stop.Token);
      socket.Deliver("PONG", "pong-1", new { serverTime = "2026-01-01T00:00:00Z" });
      await run.WaitAsync(Patience);
      Assert.True(run.IsCompletedSuccessfully);
      Assert.Equal(2, Volatile.Read(ref receives));
      Assert.Equal(1, socket.Aborts);
   }



   /// <summary>
   /// What a loop meets after the shutdown has aborted the socket decides whether the run fails. The writer is held in
   /// its send until the caller has cancelled and the socket has been aborted, and then fails. A socket that refuses the
   /// call because it has ended (disposed, or in an invalid state) is the stop itself, so the run returns. Anything
   /// else is a failure and is reported although it arrived after the cancellation: an input or output error, and a
   /// connection the server dropped, which the socket reports as closed prematurely.
   /// </summary>
   [Theory]
   [InlineData("disposed", false)]
   [InlineData("invalid-state", false)]
   [InlineData("closed-prematurely", true)]
   [InlineData("io", true)]
   public async Task FailureMetAfterTheSocketWasAbortedIsReportedUnlessItIsTheSocketRefusingTheCall(string kind, bool reported)
   {
      Exception met = kind switch
      {
         "disposed" => new ObjectDisposedException("socket"),
         "invalid-state" => new WebSocketException(WebSocketError.InvalidState),
         "closed-prematurely" => new WebSocketException(WebSocketError.ConnectionClosedPrematurely),
         _ => new IOException("Synthetic send failure."),
      };
      using var stop = new CancellationTokenSource();
      using var socket = new ScriptedWebSocket();
      socket.Sending = _ =>
      {
         stop.Cancel();
         Assert.True(SpinWait.SpinUntil(() => socket.Aborts > 0, Patience));
         throw met;
      };
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      Task run = channel.RunAsync(_ => { }, stop.Token);
      await channel.SendAsync("HEARTBEAT", new { });
      if(reported)
      {
         Assert.Same(met, await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(Patience)));
      }
      else
      {
         await run.WaitAsync(Patience);
         Assert.True(run.IsCompletedSuccessfully);
      }
   }



   /// <summary>
   /// The platform socket aborts itself as soon as the token of a call in flight is cancelled, before this channel's own
   /// shutdown begins, and from then on refuses a new call as being in an invalid state. A writer that is about to send
   /// at that moment therefore fails before the shutdown has looked at the loops. The reader is held in its callback
   /// until the shutdown aborts the socket, which it does only after a loop has ended, so the writer is the loop that
   /// ended first and had already failed when the shutdown looked. The caller had cancelled, so the refusal is still
   /// the stop and the run returns.
   /// </summary>
   [Fact]
   public async Task SocketRefusingACallBecauseTheCallerCancelledIsTheStopEvenBeforeTheShutdownBegins()
   {
      using var stop = new CancellationTokenSource();
      using var socket = new ScriptedWebSocket();
      socket.Sending = _ =>
      {
         stop.Cancel();
         throw new WebSocketException(WebSocketError.InvalidState);
      };
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      Task run = channel.RunAsync(_ => Assert.True(SpinWait.SpinUntil(() => socket.Aborts > 0, Patience)), stop.Token);
      socket.Deliver("PONG", "pong-1", new { serverTime = "2026-01-01T00:00:00Z" });
      await channel.SendAsync("HEARTBEAT", new { });
      await run.WaitAsync(Patience);
      Assert.True(run.IsCompletedSuccessfully);
   }



   /// <summary>
   /// Without a cancellation by the caller, a loop is taken to have been stopped by the shutdown only when it had not
   /// failed before the shutdown began. Nobody cancels here: the callback gives up, as the service's does when a newer
   /// connection has replaced this one, while the writer is inside a send. The shutdown then aborts the socket, and the
   /// writer's send fails on it. That refusal came after the shutdown began, so it is the shutdown itself, and the run
   /// ends as the cancellation the callback raised and not as the socket's refusal.
   /// </summary>
   [Fact]
   public async Task RefusalMetAfterTheShutdownBeganIsNotReportedAlthoughNobodyCancelled()
   {
      using var never = new CancellationTokenSource();
      using var socket = new ScriptedWebSocket();
      using var sending = new ManualResetEventSlim();
      socket.Sending = _ =>
      {
         sending.Set();
         Assert.True(SpinWait.SpinUntil(() => socket.Aborts > 0, Patience));
         return Task.CompletedTask;
      };
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      Task run = channel.RunAsync(_ =>
      {
         Assert.True(sending.Wait(Patience));
         throw new OperationCanceledException("Replaced by a newer connection.");
      }, never.Token);
      await channel.SendAsync("HEARTBEAT", new { });
      socket.Deliver("JOB_AVAILABLE", "wake-1", new { });
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Patience));
      Assert.True(run.IsCanceled);
      Assert.False(never.IsCancellationRequested);
   }



   /// <summary>A failure is not hidden by a cancellation that arrives with it: when the socket refuses a frame just as the caller cancels, the run still ends with the failure.</summary>
   [Fact]
   public async Task FailureIsReportedEvenWhenTheCallerCancelsAtTheSameMoment()
   {
      var refused = new IOException("Synthetic send failure.");
      using var stop = new CancellationTokenSource();
      using var socket = new ScriptedWebSocket
      {
         Sending = async _ =>
         {
            await stop.CancelAsync();
            throw refused;
         },
      };
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      Task run = channel.RunAsync(_ => { }, stop.Token);
      await channel.SendAsync("HEARTBEAT", new { });
      Assert.Same(refused, await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(Patience)));
   }



   /// <summary>
   /// A send can be let in while the channel is open and find it closed by the time its frame is serialized. The queue
   /// takes nothing then, and the send is told what happened, that the channel is closed, not that the queue is full:
   /// the service reports a full queue as the reason a connection dropped. The socket is not aborted on the send's
   /// account. A queue that really is full is still reported as full, and aborts the socket.
   /// </summary>
   [Fact]
   public async Task SendOvertakenByTheChannelClosingIsToldItClosedNotThatTheQueueIsFull()
   {
      using var socket = new ScriptedWebSocket();
      var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings(MaxQueuedFrames: 1));
      await channel.SendAsync("HEARTBEAT", new { });
      IOException full = await Assert.ThrowsAsync<IOException>(() => channel.SendAsync("HEARTBEAT", new { }));
      Assert.Equal("Realtime output queue is full; reconnect is required.", full.Message);
      Assert.Equal(1, socket.Aborts);

      using var second = new ScriptedWebSocket();
      var overtaken = new PrintingRealtimeChannel(second, new RealtimeSettings());
      RealtimeChannelClosedException closed = await Assert.ThrowsAsync<RealtimeChannelClosedException>(() => overtaken.SendAsync("HEARTBEAT", new ClosingPayload(overtaken.Dispose)));
      Assert.Equal("Realtime connection is closed.", closed.Message);
      Assert.Null(closed.InnerException);
      Assert.Equal(0, second.Aborts);
      channel.Dispose();
   }



   /// <summary>A payload that closes the channel while it is being serialized, which is after the send passed the closed check and before its frame is queued.</summary>
   private sealed class ClosingPayload(Action close)
   {
      /// <summary>Closes the channel the first time the serializer reads it.</summary>
      public string Text
      {
         get
         {
            close();
            return "serialized after the channel closed";
         }
      }
   }
}
