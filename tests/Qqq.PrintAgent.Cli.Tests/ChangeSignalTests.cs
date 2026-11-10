/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The signal that wakes whatever waits for a change. Every waiter must be woken once per change, a change must never
/// be lost between a waiter's look at the state and its wait, and raising must never run a waiter's code on the raising
/// thread, because changes are raised while locks are held.
/// </summary>
public sealed class ChangeSignalTests
{
   private static readonly TimeSpan s_limit = TimeSpan.FromSeconds(30);



   /// <summary>Before any change the task is pending; one change completes it, and the next task is a new, pending one.</summary>
   [Fact]
   public void OneChange_CompletesTheTaskThatWasTaken_AndTheNextTaskIsNew()
   {
      ChangeSignal signal = new();
      Task before = signal.NextChange;

      bool pendingBefore = !before.IsCompleted;
      signal.Raise();
      Task after = signal.NextChange;

      Assert.True(pendingBefore);
      Assert.Same(before, before);
      Assert.True(before.IsCompletedSuccessfully);
      Assert.NotSame(before, after);
      Assert.False(after.IsCompleted);
      Assert.Same(after, signal.NextChange);
   }



   /// <summary>
   /// Every waiter is woken by one change, and exactly once per change: a waiter that goes back to wait gets a task that
   /// only the next change completes.
   /// </summary>
   [Fact]
   public async Task EveryWaiter_IsWokenOncePerChange()
   {
      ChangeSignal signal = new();
      int[] wakes = new int[16];

      async Task WaitTwiceAsync(int waiter)
      {
         await signal.NextChange.WaitAsync(s_limit);
         Interlocked.Increment(ref wakes[waiter]);
         await signal.NextChange.WaitAsync(s_limit);
         Interlocked.Increment(ref wakes[waiter]);
      }

      Task[] waiters = Enumerable.Range(0, wakes.Length).Select(WaitTwiceAsync).ToArray();
      signal.Raise();
      await WaitUntilAsync(() => wakes.All(count => Volatile.Read(ref count) == 1));
      bool allWaitingAgain = waiters.All(waiter => !waiter.IsCompleted);
      signal.Raise();
      await Task.WhenAll(waiters).WaitAsync(s_limit);

      Assert.True(allWaitingAgain);
      Assert.All(wakes, count => Assert.Equal(2, count));
   }



   /// <summary>
   /// A change raised after a waiter has taken the task and looked at the state, but before it starts to wait, is not
   /// lost: the task it took is already complete when it gets to the wait.
   /// </summary>
   [Fact]
   public async Task ChangeBetweenTheLookAndTheWait_IsNotLost()
   {
      ChangeSignal signal = new();
      bool ready = false;

      Task taken = signal.NextChange;
      bool sawReady = ready;
      ready = true;
      signal.Raise();
      await taken.WaitAsync(s_limit);

      Assert.False(sawReady);
      Assert.True(ready);
      Assert.True(taken.IsCompletedSuccessfully);
   }



   /// <summary>
   /// The same under real concurrency. A writer raises a change for every step of a counter while several waiters follow
   /// it with the take, look, wait pattern. Every waiter reaches the last step: had a change been lost between a look and
   /// a wait, a waiter would wait for a change that never comes after the writer has finished.
   /// </summary>
   [Fact]
   public async Task WaitersFollowingACounter_NeverMissItsLastChange()
   {
      ChangeSignal signal = new();
      const int steps = 20_000;
      int counter = 0;

      async Task<int> FollowAsync()
      {
         int seen = 0;
         while(true)
         {
            Task changed = signal.NextChange;
            int now = Volatile.Read(ref counter);
            seen += now > 0 ? 1 : 0;
            if(now == steps)
            { return seen; }
            await changed.WaitAsync(s_limit);
         }
      }

      Task<int>[] followers = Enumerable.Range(0, 8).Select(_ => Task.Run(FollowAsync)).ToArray();
      await Task.Run(() =>
      {
         for(int step = 0; step < steps; step++)
         {
            Interlocked.Increment(ref counter);
            signal.Raise();
         }
      });
      int[] looks = await Task.WhenAll(followers).WaitAsync(s_limit);

      Assert.All(looks, count => Assert.True(count >= 1));
      Assert.False(signal.NextChange.IsCompleted);
   }



   /// <summary>Changes raised from several threads at once complete a task taken before them, and leave a fresh task behind.</summary>
   [Fact]
   public async Task ConcurrentChanges_CompleteTheTakenTask()
   {
      ChangeSignal signal = new();
      Task taken = signal.NextChange;

      await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(signal.Raise)));

      Assert.True(taken.IsCompletedSuccessfully);
      Assert.False(signal.NextChange.IsCompleted);
   }



   /// <summary>
   /// Raising does not run a waiter's continuation on the raising thread, even one that asks to run synchronously, so a
   /// change raised while a lock is held never runs waiter code inside that lock. The raising thread stays in the lock
   /// until the continuation has run somewhere else and has found the lock taken.
   /// </summary>
   [Fact]
   public void Raising_NeverRunsWaiterCodeOnTheRaisingThread()
   {
      ChangeSignal signal = new();
      Lock held = new();
      using ManualResetEventSlim ran = new();
      int raiser = Environment.CurrentManagedThreadId;
      int continuationThread = 0;
      bool enteredTheLock = true;
      _ = signal.NextChange.ContinueWith(_ =>
      {
         continuationThread = Environment.CurrentManagedThreadId;
         enteredTheLock = held.TryEnter();
         ran.Set();
      }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

      bool finished;
      lock(held)
      {
         signal.Raise();
         finished = ran.Wait(s_limit);
      }

      Assert.True(finished);
      Assert.NotEqual(raiser, continuationThread);
      Assert.False(enteredTheLock);
   }



   /// <summary>A one-shot signal made for a single event completes once and runs its continuations asynchronously as well.</summary>
   [Fact]
   public void OneShot_CompletesOnceAndAsynchronously()
   {
      TaskCompletionSource source = ChangeSignal.OneShot();

      Assert.NotSame(source, ChangeSignal.OneShot());
      Assert.True(source.TrySetResult());
      Assert.False(source.TrySetResult());
      Assert.Equal(TaskCreationOptions.RunContinuationsAsynchronously, source.Task.CreationOptions & TaskCreationOptions.RunContinuationsAsynchronously);
   }



   /// <summary>Waits for a condition that other threads bring about, by yielding and looking again, and fails instead of hanging when it never comes.</summary>
   private static async Task WaitUntilAsync(Func<bool> condition)
   {
      using CancellationTokenSource limit = new(s_limit);
      while(!condition())
      {
         limit.Token.ThrowIfCancellationRequested();
         await Task.Yield();
      }
   }
}
