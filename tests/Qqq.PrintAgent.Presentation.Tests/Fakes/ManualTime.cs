/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Tests.Fakes;

/// <summary>A clock that moves only when a test advances it. Timers created through it (such as a cancellation time limit) fire during <see cref="Advance"/>, so a test reaches a three-minute limit instantly and deterministically.</summary>
internal sealed class ManualTime : TimeProvider
{
   private readonly List<ManualTimer> _timers = [];
   private TimeSpan _elapsed;



   /// <summary>The due times of every timer requested so far, which is how a test reads the time limit a caller asked for.</summary>
   internal List<TimeSpan> RequestedDelays { get; } = [];



   /// <summary>Creates a timer that fires once when the manual clock has advanced past its due time.</summary>
   public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
   {
      RequestedDelays.Add(dueTime);
      ManualTimer timer = new(callback, state, _elapsed + dueTime);
      _timers.Add(timer);
      return timer;
   }



   /// <summary>Moves the clock forward and fires every timer that became due.</summary>
   internal void Advance(TimeSpan by)
   {
      _elapsed += by;
      foreach(ManualTimer timer in _timers.ToArray())
      {
         timer.FireIfDue(_elapsed);
      }
   }



   /// <summary>One pending timer of the manual clock.</summary>
   private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
   {
      private bool _active = true;



      /// <summary>Runs the callback once when the clock has reached the due time and the timer was not disposed.</summary>
      internal void FireIfDue(TimeSpan elapsed)
      {
         if(_active && elapsed >= due)
         {
            _active = false;
            callback(state);
         }
      }



      /// <summary>Rescheduling is not needed by the code under test; the timer keeps its original due time.</summary>
      public bool Change(TimeSpan dueTime, TimeSpan period) => _active;



      /// <summary>Stops the timer so it never fires.</summary>
      public void Dispose() => _active = false;



      /// <summary>Stops the timer so it never fires.</summary>
      public ValueTask DisposeAsync()
      {
         Dispose();
         return ValueTask.CompletedTask;
      }
   }
}
