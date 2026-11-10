/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Tells everything that is waiting that something changed, such as freed capacity or changed readiness, without a timer
/// and without naming the waiters. A waiter takes <see cref="NextChange"/>, looks at the state it cares about, and awaits
/// the task only when the state does not let it proceed. Whoever changes that state calls <see cref="Raise"/> afterwards.
/// Each change completes the task every waiter held at that moment, once, and gives later arrivals a fresh one.
/// </summary>
internal sealed class ChangeSignal
{
   private TaskCompletionSource _current = OneShot();



   /// <summary>
   /// The task that completes at the next change. Take it before looking at the state, or inside the same lock the state
   /// changes under: a change made after the look then completes the task that was taken, so it cannot be missed.
   /// </summary>
   internal Task NextChange => Volatile.Read(ref _current).Task;



   /// <summary>
   /// Wakes everything waiting for a change. A fresh signal is swapped in and the old one completed, so a waiter holding
   /// the old signal is released and a later arrival waits for the next change instead of missing it. Continuations run
   /// asynchronously, so raising under a lock never runs waiter code inside that lock.
   /// </summary>
   internal void Raise() => Interlocked.Exchange(ref _current, OneShot()).TrySetResult();



   /// <summary>Creates a one-shot signal whose continuations run asynchronously, so completing it under a lock never runs waiter code inside that lock.</summary>
   internal static TaskCompletionSource OneShot() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
