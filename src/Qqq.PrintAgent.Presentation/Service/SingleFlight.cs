/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;

namespace Qqq.PrintAgent.Presentation.Service;

/// <summary>
/// Runs one piece of work at a time and lets callers share its outcome, the way the contract defines single-flight
/// for <c>printers.discover</c> in the service. The client uses it for the two reads it repeats on a timer and that
/// take no arguments, so a slow answer and a second tick, or two pages asking at once, cost one request and not one
/// each.
/// <para>
/// The flights are counted as they start, which is what lets a caller ask for an outcome that is newer than a moment
/// it names: it notes the count (<see cref="Started"/>), and a flight with a higher number started after that. A
/// caller that names no moment shares the flight that is in the air, whenever it started.
/// </para>
/// </summary>
internal sealed class SingleFlight<TOutcome>
{
   /// <summary>Guards the two members below.</summary>
   private readonly Lock _gate = new();



   /// <summary>The flight that was started last, in the air or landed.</summary>
   private Task<TOutcome>? _last;



   /// <summary>How many flights have been started, which is the number of <see cref="_last"/>.</summary>
   private long _started;



   /// <summary>Says how many flights have been started so far and gives the one that is in the air, if one is. A flight that starts after this call has a higher number than the one returned.</summary>
   internal long Started(out Task? inTheAir)
   {
      lock(_gate)
      {
         inTheAir = _last is { IsCompleted: false } ? _last : null;
         return _started;
      }
   }



   /// <summary>
   /// Gives the flight a caller can share, or says that it has to start one. A caller that names no moment shares
   /// the flight in the air. A caller that names one (<paramref name="startedAfter"/>, a count it was given by
   /// <see cref="Started"/>) shares the last flight when that started later, in the air or landed, and never an
   /// earlier one.
   /// </summary>
   internal bool TryShare(long? startedAfter, [NotNullWhen(true)] out Task<TOutcome>? flight)
   {
      lock(_gate)
      {
         return TryFind(startedAfter, out flight);
      }
   }



   /// <summary>
   /// Returns the flight the caller can share (<see cref="TryShare"/>), or starts one and says so. The work is
   /// started on the calling thread, after the lock is released, so nothing the work does can run while the lock is
   /// held; a caller that arrives in between shares the flight that is about to start. Work that fails is shared as
   /// a failure in the same way.
   /// </summary>
   internal Task<TOutcome> ShareOrStart(long? startedAfter, Func<Task<TOutcome>> start, out bool started)
   {
      Task<Task<TOutcome>> starter;
      Task<TOutcome> flight;
      lock(_gate)
      {
         if(TryFind(startedAfter, out Task<TOutcome>? shared))
         {
            started = false;
            return shared;
         }
         starter = new Task<Task<TOutcome>>(start);
         _last = flight = starter.Unwrap();
         _started++;
      }
      starter.RunSynchronously(TaskScheduler.Default);
      started = true;
      return flight;
   }



   /// <summary>Gives the flight that serves a caller, if there is one. The caller holds the lock.</summary>
   private bool TryFind(long? startedAfter, [NotNullWhen(true)] out Task<TOutcome>? flight)
   {
      flight = _last is { } last && (startedAfter is { } count ? _started > count : !last.IsCompleted) ? last : null;
      return flight is not null;
   }
}
