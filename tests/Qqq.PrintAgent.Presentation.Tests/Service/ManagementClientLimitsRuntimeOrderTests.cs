/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;

namespace Qqq.PrintAgent.Presentation.Tests.Service;

/// <summary>
/// Two things the .NET runtime does and this product does not decide, which three tests of
/// <see cref="ManagementClientLimitsTests"/> use to bring about a moment they could not otherwise reach: a caller
/// that leaves at the very instant its turn comes or the snapshot it waits out lands, and a place that is given back
/// while an answer is still being delivered. Those three are written to pass on a runtime that orders these events
/// otherwise, and there they would pass without testing what they are named for. The tests here fail on such a
/// runtime, and each says which of the three lost their meaning. Each waits in the same way the client does.
/// </summary>
public sealed class ManagementClientLimitsRuntimeOrderTests
{
   /// <summary>How long a test waits for something that must happen on another thread before it fails instead of hanging the run. It is a guard, never what a test asserts.</summary>
   private static readonly TimeSpan s_guard = TimeSpan.FromSeconds(30);



   /// <summary>
   /// A cancellation token runs its callbacks newest first, one after the other, and a cancellation that is awaited
   /// runs them on a thread that has no synchronization context. So a callback a test registers last runs before
   /// the callbacks of those already waiting on the token: before a wait for a turn is told (it registers as the
   /// first callback here does) and before a wait for a task is told (the wait in the middle here), and what that
   /// callback completes goes on inside it.
   /// </summary>
   [Fact]
   public async Task CancellationCallbacksRunNewestFirst_OrTwoTestsOfACallerLeavingAtTheLastMomentTestNothing()
   {
      using CancellationTokenSource leaving = new();
      List<(string Callback, bool TheWaitWasTold, bool OnAThreadWithNoContext)> ran = [];
      Task neverLands = new TaskCompletionSource().Task;
      Task wait = null!;
      using CancellationTokenRegistration oldest = leaving.Token.UnsafeRegister(_ => ran.Add(("oldest", wait.IsCompleted, SynchronizationContext.Current is null)), null);
      wait = neverLands.WaitAsync(leaving.Token);
      using CancellationTokenRegistration newest = leaving.Token.Register(() => ran.Add(("newest", wait.IsCompleted, SynchronizationContext.Current is null)));

      await leaving.CancelAsync();

      Assert.True(wait.IsCanceled);
      Assert.True(ran.SequenceEqual([("newest", false, true), ("oldest", true, true)]),
         $"A cancellation ran its callbacks as {string.Join(", ", ran)}, and no longer newest first on a thread with no synchronization context. "
         + $"{nameof(ManagementClientLimitsTests.ChangeWhoseTurnComesAsItsCallerLeaves_IsNotSent_AndItsPlaceIsGivenBack)} and "
         + $"{nameof(ManagementClientLimitsTests.SnapshotSentAfterTheCall_WhoseCallerLeavesAsTheOneInFlightLands_HasSentNothing)} still pass, and no longer reach the moment they are named for.");
   }



   /// <summary>
   /// When a task is completed on a thread that has no synchronization context, those waiting for it go on inside
   /// the call that completes it, in the order their waits began: first the method that awaits the task itself, as
   /// the client awaits the pipe, and then, as that method's own task completes, each wait that can be cancelled,
   /// as a caller waits out a snapshot in flight. When the completing call returns, all of them have gone on.
   /// </summary>
   [Fact]
   public async Task WaitsGoOnInOrderInsideTheCompletingCall_OrThreeTestsThatDecideTheirOrderOfEventsThatWayTestNothing()
   {
      TaskCompletionSource<int> held = new();
      using CancellationTokenSource first = new();
      using CancellationTokenSource second = new();
      ConcurrentQueue<string> wentOn = new();
      Task<int> flight = AnsweredAsync(held.Task, wentOn);
      Task[] waits = [WaitedOutAsync(flight, "the first wait", wentOn, first.Token), WaitedOutAsync(flight, "the second wait", wentOn, second.Token)];
      string[] insideTheCall = [];

      await Task.Run(() =>
      {
         held.SetResult(0);
         insideTheCall = [.. wentOn];
      });
      await Task.WhenAll(waits).WaitAsync(s_guard);

      Assert.True(insideTheCall.SequenceEqual(["the request", "the first wait", "the second wait"]),
         $"Inside the call that completed the task, these went on: {string.Join(", ", insideTheCall)}. They no longer all go on there, in the order they began to wait. "
         + $"{nameof(ManagementClientLimitsTests.SnapshotSentAfterTheCall_WhoseCallerLeavesAsTheOneInFlightLands_HasSentNothing)}, "
         + $"{nameof(ManagementClientLimitsTests.ChangeWhoseTurnComesAsItsCallerLeaves_IsNotSent_AndItsPlaceIsGivenBack)} and "
         + $"{nameof(ManagementClientLimitsTests.RequestThatWaitedItsTurn_GoesOnOutsideTheCallThatGaveItItsPlace)} still pass, and no longer reach the moment they are named for.");
   }



   /// <summary>Awaits a held reply as the client awaits the pipe, and notes that it went on.</summary>
   private static async Task<int> AnsweredAsync(Task<int> held, ConcurrentQueue<string> wentOn)
   {
      int reply = await held.ConfigureAwait(false);
      wentOn.Enqueue("the request");
      return reply;
   }



   /// <summary>Waits out a flight as a caller that can leave does, and notes that it went on.</summary>
   private static async Task WaitedOutAsync(Task flight, string name, ConcurrentQueue<string> wentOn, CancellationToken cancellationToken)
   {
      await flight.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
      wentOn.Enqueue(name);
   }
}
