/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// A clock that moves only when a test advances it. A wait made through it (<c>Task.Delay</c> with a time provider creates a
/// timer here) ends during <see cref="Advance"/> instead of after real time has passed, so a test can let exactly one
/// heartbeat interval elapse, and can first see that the code under test has actually started waiting.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
   private readonly Lock _lock = new();
   private readonly List<ManualTimer> _timers = [];
   private DateTimeOffset _now = DateTimeOffset.UtcNow;



   /// <summary>The manual time; it starts at the real time of creation so values compared with the system clock stay plausible.</summary>
   public override DateTimeOffset GetUtcNow()
   {
      lock(_lock)
      { return _now; }
   }



   /// <summary>Elapsed-time readings follow the manual clock too, in ticks, so rates and durations do not drift from it.</summary>
   public override long GetTimestamp() => GetUtcNow().UtcTicks;



   /// <summary>The unit of <see cref="GetTimestamp"/>: one tick, as for a date and time.</summary>
   public override long TimestampFrequency => TimeSpan.TicksPerSecond;



   /// <summary>How many waits are armed and have not fired, which tells a test that the code under test has reached its wait.</summary>
   internal int PendingTimers
   {
      get
      {
         lock(_lock)
         { return _timers.Count(timer => timer.Due is not null); }
      }
   }



   /// <summary>
   /// True when a wait armed at the present instant will end after exactly the given time. It names one wait among several:
   /// a loop that has just started waiting for its interval, as opposed to a longer deadline or a wait armed earlier.
   /// </summary>
   internal bool HasTimerDueIn(TimeSpan delay)
   {
      lock(_lock)
      { return _timers.Any(timer => timer.Due == _now + delay); }
   }



   /// <summary>
   /// Moves the clock forward and fires every wait that has come due. The callbacks run after the lock is released, because a
   /// callback may create or dispose a timer on this same clock.
   /// </summary>
   internal void Advance(TimeSpan elapsed)
   {
      List<ManualTimer> due;
      lock(_lock)
      {
         _now += elapsed;
         due = _timers.Where(timer => timer.Due <= _now).ToList();
         foreach(ManualTimer timer in due)
         { timer.Due = timer.Period is TimeSpan period ? _now + period : null; }
      }
      foreach(ManualTimer timer in due)
      { timer.Fire(); }
   }



   /// <summary>Creates a timer that fires only from <see cref="Advance"/>.</summary>
   public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
   {
      var timer = new ManualTimer(this, callback, state);
      lock(_lock)
      { _timers.Add(timer); }
      _ = timer.Change(dueTime, period);
      return timer;
   }



   /// <summary>One wait on the manual clock: the moment it is due (null when it is not armed) and its repeat interval, if any.</summary>
   private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
   {
      internal DateTimeOffset? Due { get; set; }

      internal TimeSpan? Period { get; private set; }



      /// <summary>Runs the callback the creator supplied.</summary>
      internal void Fire() => callback(state);



      /// <summary>Arms the timer relative to the manual time, or disarms it when the due time is infinite.</summary>
      public bool Change(TimeSpan dueTime, TimeSpan period)
      {
         lock(owner._lock)
         {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            Period = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? null : period;
         }
         return true;
      }



      /// <summary>Removes the timer, so a cancelled wait no longer counts as pending.</summary>
      public void Dispose()
      {
         lock(owner._lock)
         { _ = owner._timers.Remove(this); }
      }



      /// <summary>Removes the timer; nothing here is asynchronous.</summary>
      public ValueTask DisposeAsync()
      {
         Dispose();
         return ValueTask.CompletedTask;
      }
   }
}
