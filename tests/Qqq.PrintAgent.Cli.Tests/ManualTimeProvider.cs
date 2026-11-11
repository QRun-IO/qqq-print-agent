/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// A clock that moves only when a test advances it. A wait made through it (<c>Task.Delay</c> with a time provider creates a
/// timer here) ends during <see cref="Advance"/> instead of after real time has passed, so a test can let exactly one
/// heartbeat interval elapse, and can first see that the code under test has actually started waiting. Unless a test
/// says otherwise it starts at the real time and uses the machine's time zone; a test of code that writes or reads local
/// time gives it a fixed instant and a fixed zone, so the result does not depend on when or where the test runs.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset? start = null, TimeZoneInfo? localTimeZone = null) : TimeProvider
{
   private readonly Lock _lock = new();
   private readonly List<ManualTimer> _timers = [];
   private DateTimeOffset _now = (start ?? DateTimeOffset.UtcNow).ToUniversalTime();



   /// <summary>The zone local time is given in: the one the test fixed, or the machine's when the test fixed none.</summary>
   public override TimeZoneInfo LocalTimeZone => localTimeZone ?? base.LocalTimeZone;



   /// <summary>The manual time; unless the test fixed a start it begins at the real time of creation, so values compared with the system clock stay plausible.</summary>
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
   /// How long remains of a pending wait that was armed for exactly the given time, or null when there is none. Unlike
   /// <see cref="HasTimerDueIn"/> it finds the wait whenever it was armed, so it still names a loop's interval after a
   /// test has moved the clock by less than that interval.
   /// </summary>
   internal TimeSpan? RemainingOfWaitArmedFor(TimeSpan delay)
   {
      lock(_lock)
      {
         ManualTimer? timer = _timers.Where(candidate => candidate.Due is not null && candidate.ArmedFor == delay).MinBy(candidate => candidate.Due);
         return timer is null ? null : timer.Due - _now;
      }
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
      /// <summary>The manual time at which the timer fires next, or null while it is not armed.</summary>
      internal DateTimeOffset? Due { get; set; }



      /// <summary>The interval at which the timer fires again after its first time, or null when it fires once.</summary>
      internal TimeSpan? Period { get; private set; }



      /// <summary>The time the timer was last armed for, which tells waits of different lengths apart whenever they were armed.</summary>
      internal TimeSpan? ArmedFor { get; private set; }



      /// <summary>Runs the callback the creator supplied.</summary>
      internal void Fire() => callback(state);



      /// <summary>Arms the timer relative to the manual time, or disarms it when the due time is infinite.</summary>
      public bool Change(TimeSpan dueTime, TimeSpan period)
      {
         lock(owner._lock)
         {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            ArmedFor = dueTime == Timeout.InfiniteTimeSpan ? null : dueTime;
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
