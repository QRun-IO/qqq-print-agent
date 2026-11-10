/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;

namespace Qqq.PrintAgent.Presentation.Tests.Fakes;

/// <summary>
/// A single-threaded synchronization context like a UI thread's: work posted to it runs, in order, on the thread that
/// called <see cref="Run"/>. A test uses it to prove that code which awaits something finished on another thread comes
/// back to the thread it started on.
/// </summary>
internal sealed class PumpContext : SynchronizationContext, IDisposable
{
   private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _work = [];



   /// <summary>Queues work for the pumping thread; work posted after the pump stopped is dropped.</summary>
   public override void Post(SendOrPostCallback d, object? state)
   {
      try
      {
         _work.Add((d, state));
      }
      catch(InvalidOperationException)
      {
         // The body finished and the pump stopped; nothing is waiting for this continuation.
      }
   }



   /// <summary>
   /// Runs an asynchronous body on the calling thread with this context installed and pumps its continuations until it
   /// finishes, then rethrows its failure, if any. A body that never finishes fails the test after a generous limit instead
   /// of hanging the run.
   /// </summary>
   internal void Run(Func<Task> body)
   {
      SynchronizationContext? previous = Current;
      SetSynchronizationContext(this);
      try
      {
         using CancellationTokenSource limit = new(TimeSpan.FromSeconds(30));
         Task task = body();
         _ = task.ContinueWith(_ => _work.CompleteAdding(), TaskScheduler.Default);
         foreach((SendOrPostCallback callback, object? state) in _work.GetConsumingEnumerable(limit.Token))
         {
            callback(state);
         }
         task.GetAwaiter().GetResult();
      }
      finally
      {
         SetSynchronizationContext(previous);
      }
   }



   /// <summary>Releases the work queue.</summary>
   public void Dispose() => _work.Dispose();
}
