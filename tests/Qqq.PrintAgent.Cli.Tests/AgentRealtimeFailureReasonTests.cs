/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Which failure is reported as the reason a realtime connection ended. Several of a connection's tasks usually fail together,
/// and the one that fails first in time is not always the cause, so the choice is made explicitly and tested here without a socket.
/// </summary>
public sealed class AgentRealtimeFailureReasonTests
{
   /// <summary>
   /// When the reader rejects a frame, the channel fails every send that was waiting for an acknowledgement before the reader's
   /// own task has finished. The secondary failure therefore completes first; the reader's failure must still be the one reported.
   /// </summary>
   [Fact]
   public async Task ReaderFailureIsReportedEvenWhenASecondaryFailureCompletesFirst()
   {
      var reader = new TaskCompletionSource();
      var sender = new TaskCompletionSource();
      var idle = new TaskCompletionSource();
      Task completion = AgentRuntime.CompleteConnectionAsync(reader.Task, sender.Task, idle.Task);
      sender.SetException(new IOException("Realtime connection closed before acknowledgement; durable receipts will be replayed."));
      idle.SetCanceled();
      Assert.False(completion.IsCompleted);
      reader.SetException(new InvalidDataException("The PRINTERS frame has no printers list."));
      InvalidDataException reported = await Assert.ThrowsAsync<InvalidDataException>(() => completion);
      Assert.Equal("The PRINTERS frame has no printers list.", reported.Message);
   }



   /// <summary>When another task ends the connection, the reader is only cancelled by the teardown, so that task's failure is the reason.</summary>
   [Fact]
   public async Task WorkerFailureIsReportedWhenTheReaderWasOnlyCancelled()
   {
      var reader = new TaskCompletionSource();
      var worker = new TaskCompletionSource();
      Task completion = AgentRuntime.CompleteConnectionAsync(reader.Task, worker.Task);
      worker.SetException(new TimeoutException("Synthetic acknowledgement timeout."));
      reader.SetCanceled();
      _ = await Assert.ThrowsAsync<TimeoutException>(() => completion);
   }



   /// <summary>A connection that was only cancelled (shutdown, sign-in, an explicit reconnect) reports cancellation, not a failure.</summary>
   [Fact]
   public async Task CancellationIsReportedWhenNothingFailed()
   {
      var reader = new TaskCompletionSource();
      var worker = new TaskCompletionSource();
      Task completion = AgentRuntime.CompleteConnectionAsync(reader.Task, worker.Task);
      reader.SetCanceled();
      worker.SetCanceled();
      _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
   }
}
