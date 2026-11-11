/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Service;

/// <summary>
/// Runs one piece of work at a time and lets everyone who asks while it runs share its outcome, the way the contract
/// defines single-flight for <c>printers.discover</c> in the service. The client uses it for the two reads it repeats
/// on a timer and that take no arguments, so a slow answer and a second tick, or two pages asking at once, cost one
/// request and not one each.
/// </summary>
internal sealed class SingleFlight<TOutcome>
{
   /// <summary>Guards <see cref="_running"/>.</summary>
   private readonly Lock _gate = new();



   /// <summary>The work that was started last. While it has not finished, it is the flight everyone joins.</summary>
   private Task<TOutcome>? _running;



   /// <summary>
   /// Returns the work that is running, or starts it when none is. The work is started on the calling thread, after the
   /// lock is released, so nothing the work does can run while the lock is held; a caller that arrives in between joins
   /// the flight that is about to start. Work that fails is shared as a failure in the same way.
   /// </summary>
   internal Task<TOutcome> Join(Func<Task<TOutcome>> start)
   {
      Task<Task<TOutcome>> starter;
      Task<TOutcome> flight;
      lock(_gate)
      {
         if(_running is { IsCompleted: false } running)
         {
            return running;
         }
         starter = new Task<Task<TOutcome>>(start);
         _running = flight = starter.Unwrap();
      }
      starter.RunSynchronously(TaskScheduler.Default);
      return flight;
   }
}
