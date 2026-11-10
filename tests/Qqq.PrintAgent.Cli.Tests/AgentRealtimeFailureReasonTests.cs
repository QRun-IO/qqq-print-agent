/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Which failure is reported as the reason a realtime connection ended. Several of a connection's tasks usually fail together,
/// and which of them completes first is a matter of scheduling, so the choice is made by rule. The tests complete tasks by
/// hand, in the order that would mislead. That a real channel gives its failed sends the channel-closed type and the cause
/// is tested with the channel, in the Core tests.
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
   /// completes first, but its channel-closed failure only says the connection was gone. The reader's failure is the reason,
   /// and the failed send, which carries that failure as its cause, is detail.
   /// </summary>
   [Fact]
   public async Task ReaderFailureIsTheReasonWhenAWorkerOnlyFoundTheConnectionGone()
   {
      var frame = new InvalidDataException("The PRINTERS frame has no printers list.");
      var reader = new TaskCompletionSource();
      var sender = new TaskCompletionSource();
      List<Exception> detail = [];
      Task completion = AgentRuntime.CompleteConnectionAsync(detail.Add, reader.Task, sender.Task);
      sender.SetException(new RealtimeChannelClosedException("Realtime connection closed before acknowledgement; durable receipts will be replayed.", frame));
      Assert.False(completion.IsCompleted);
      reader.SetException(frame);
      Assert.Same(frame, await Assert.ThrowsAsync<InvalidDataException>(() => completion));
      Assert.Same(frame, Assert.Single(detail).InnerException);
   }



   /// <summary>
   /// A worker could not queue a message because the output queue was full, and the channel aborted the socket, which made the
   /// reader fail as well, first. The full queue is a plain I/O error by type but it is why the connection ended, so it is the
   /// reason and the reader's failure is detail.
   /// </summary>
   [Fact]
   public async Task WorkersOwnPlainIoFailureComesBeforeTheReadersEvenWhenTheReaderFailedFirst()
   {
      var reader = new TaskCompletionSource();
      var worker = new TaskCompletionSource();
      List<Exception> detail = [];
      Task completion = AgentRuntime.CompleteConnectionAsync(detail.Add, reader.Task, worker.Task);
      reader.SetException(new System.Net.WebSockets.WebSocketException("The remote party closed the WebSocket connection without completing the close handshake."));
      Assert.False(completion.IsCompleted);
      var full = new IOException("Realtime output queue is full; reconnect is required.");
      worker.SetException(full);
      Assert.Same(full, await Assert.ThrowsAsync<IOException>(() => completion));
      _ = Assert.IsType<System.Net.WebSockets.WebSocketException>(Assert.Single(detail));
   }



   /// <summary>A worker that only found the connection unavailable, with no cause to carry, is still detail: the reader's failure is the reason.</summary>
   [Fact]
   public async Task WorkerThatFoundNoConnectionAtAllIsDetail()
   {
      var reader = new TaskCompletionSource();
      var worker = new TaskCompletionSource();
      List<Exception> detail = [];
      Task completion = AgentRuntime.CompleteConnectionAsync(detail.Add, reader.Task, worker.Task);
      worker.SetException(new RealtimeChannelClosedException("The realtime connection is unavailable.", null));
      var regressed = new InvalidDataException("Desired state revision regressed.");
      reader.SetException(regressed);
      Assert.Same(regressed, await Assert.ThrowsAsync<InvalidDataException>(() => completion));
      _ = Assert.IsType<RealtimeChannelClosedException>(Assert.Single(detail));
   }



   /// <summary>A session the server ended is an I/O error by type but a reason of its own, so it comes before the reader's failure like any other worker failure that is not the channel-closed one.</summary>
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
   /// The channel's run returns when its caller cancels it, so in an orderly teardown the reader is not cancelled: it
   /// completes. The workers are cancelled by the same token, so the connection still reports cancellation, not a
   /// normal end and not a failure.
   /// </summary>
   [Fact]
   public async Task CancellationIsReportedWhenTheReaderCompletedAndTheWorkersWereCancelled()
   {
      var reader = new TaskCompletionSource();
      var worker = new TaskCompletionSource();
      var watchdog = new TaskCompletionSource();
      List<Exception> detail = [];
      Task completion = AgentRuntime.CompleteConnectionAsync(detail.Add, reader.Task, worker.Task, watchdog.Task);
      reader.SetResult();
      worker.SetCanceled();
      watchdog.SetCanceled();
      _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
      Assert.Empty(detail);
   }



   /// <summary>A reader that completed in the teardown does not hide what ended the connection: a worker's failure is still the reason.</summary>
   [Fact]
   public async Task WorkerFailureIsTheReasonWhenTheReaderCompleted()
   {
      var reader = new TaskCompletionSource();
      var worker = new TaskCompletionSource();
      List<Exception> detail = [];
      Task completion = AgentRuntime.CompleteConnectionAsync(detail.Add, reader.Task, worker.Task);
      var failure = new TimeoutException("Synthetic acknowledgement timeout.");
      worker.SetException(failure);
      reader.SetResult();
      Assert.Same(failure, await Assert.ThrowsAsync<TimeoutException>(() => completion));
      Assert.Empty(detail);
   }
}
