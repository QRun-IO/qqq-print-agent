/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// A clock that moves only when a test says so, including its timers, so a twenty second request limit or a fifteen
/// second acknowledgement wait is reached by calling <see cref="Advance"/> and never by waiting. Timers created through
/// it (by <c>Task.Delay</c>, <c>WaitAsync</c> or a timed cancellation source) fire, in due order, on the thread that
/// advances the clock.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
   private readonly Lock _lock = new();
   private readonly List<ManualTimer> _timers = [];
   private readonly List<(Func<IReadOnlyList<TimeSpan>, bool> Condition, TaskCompletionSource Waiter)> _waiters = [];
   private readonly DateTimeOffset _epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
   private long _ticks;



   /// <summary>One timestamp unit per tick, so timestamps and <see cref="TimeSpan"/> ticks are the same scale.</summary>
   public override long TimestampFrequency => TimeSpan.TicksPerSecond;



   /// <summary>UTC, so local time never depends on the machine running the tests.</summary>
   public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;



   /// <summary>The time elapsed on this clock since it was created.</summary>
   internal TimeSpan Elapsed
   {
      get
      {
         lock(_lock)
         { return TimeSpan.FromTicks(_ticks); }
      }
   }



   /// <summary>How long from now each armed timer is due, soonest first, which tells a test exactly what the code under test is waiting for.</summary>
   internal IReadOnlyList<TimeSpan> DueIn
   {
      get
      {
         lock(_lock)
         { return DueInLocked(); }
      }
   }



   /// <summary>
   /// Completes once the armed timers satisfy the condition, checked whenever a timer is created, changed or removed.
   /// A test uses it to know the code under test has started waiting on the clock before advancing it, with no polling
   /// and no sleep. The ten second bound only stops a broken test from hanging.
   /// </summary>
   internal Task WhenAsync(Func<IReadOnlyList<TimeSpan>, bool> condition)
   {
      lock(_lock)
      {
         if(condition(DueInLocked()))
         { return Task.CompletedTask; }
         var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
         _waiters.Add((condition, waiter));
         return waiter.Task.WaitAsync(TimeSpan.FromSeconds(10));
      }
   }



   /// <summary>Completes once a timer is armed that falls due exactly this long from now.</summary>
   internal Task WhenDueInAsync(TimeSpan delay) => WhenAsync(due => due.Contains(delay));



   /// <summary>The armed timers' remaining times; the caller holds the lock.</summary>
   private TimeSpan[] DueInLocked() => [.. _timers.Where(timer => timer.Due is not null).Select(timer => TimeSpan.FromTicks(timer.Due!.Value - _ticks)).Order()];



   /// <summary>Releases every waiter whose condition the armed timers now satisfy; the caller holds the lock.</summary>
   private void SignalLocked()
   {
      TimeSpan[] due = DueInLocked();
      foreach((Func<IReadOnlyList<TimeSpan>, bool> condition, TaskCompletionSource waiter) in _waiters.ToArray())
      {
         if(condition(due))
         {
            _waiters.Remove((condition, waiter));
            waiter.TrySetResult();
         }
      }
   }



   /// <summary>The current monotonic timestamp.</summary>
   public override long GetTimestamp()
   {
      lock(_lock)
      { return _ticks; }
   }



   /// <summary>The wall clock: a fixed start on 1 January 2026 UTC plus the time advanced so far.</summary>
   public override DateTimeOffset GetUtcNow() => _epoch + Elapsed;



   /// <summary>Creates a timer that fires only when the clock is advanced past its due time.</summary>
   public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
   {
      var timer = new ManualTimer(this, callback, state);
      lock(_lock)
      { _timers.Add(timer); }
      timer.Change(dueTime, period);
      return timer;
   }



   /// <summary>
   /// Moves the clock forward and runs every timer that falls due on the way, one at a time and in due order, with the
   /// clock standing at each timer's own due time while its callback runs. A callback runs outside the lock, so it may
   /// create or change timers.
   /// </summary>
   internal void Advance(TimeSpan amount)
   {
      ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);
      long target;
      lock(_lock)
      { target = _ticks + amount.Ticks; }
      while(true)
      {
         ManualTimer? due;
         lock(_lock)
         {
            due = _timers.Where(timer => timer.Due <= target).MinBy(timer => timer.Due);
            if(due is null)
            {
               _ticks = target;
               return;
            }
            _ticks = Math.Max(_ticks, due.Due!.Value);
            due.Due = due.Period is { } period ? due.Due + period : null;
         }
         due.Fire();
      }
   }



   /// <summary>One timer on the manual clock: its due time in clock ticks (null while disarmed) and its repeat period.</summary>
   private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
   {
      /// <summary>The clock tick at which the timer next fires, or null while it is disarmed.</summary>
      internal long? Due { get; set; }



      /// <summary>The repeat interval in ticks, or null for a timer that fires once.</summary>
      internal long? Period { get; private set; }



      /// <summary>Runs the callback, as the system timer would on a pool thread.</summary>
      internal void Fire() => callback(state);



      /// <summary>Re-arms the timer relative to the clock's current time; an infinite due time disarms it.</summary>
      public bool Change(TimeSpan dueTime, TimeSpan period)
      {
         lock(owner._lock)
         {
            if(!owner._timers.Contains(this))
            { return false; }
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._ticks + dueTime.Ticks;
            Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period.Ticks;
            owner.SignalLocked();
            return true;
         }
      }



      /// <summary>Removes the timer, so it can never fire again.</summary>
      public void Dispose()
      {
         lock(owner._lock)
         {
            owner._timers.Remove(this);
            owner.SignalLocked();
         }
      }



      /// <summary>Removes the timer; there is nothing asynchronous to wait for.</summary>
      public ValueTask DisposeAsync()
      {
         Dispose();
         return ValueTask.CompletedTask;
      }
   }
}
