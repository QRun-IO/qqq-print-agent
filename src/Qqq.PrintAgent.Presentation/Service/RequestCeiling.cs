/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Service;

/// <summary>
/// Counts the requests one client has in flight and holds them to a limit. A request is in flight from the moment it
/// is handed to the pipe until the pipe is done with it, whether or not anyone still waits for the answer, because that
/// is how long it occupies one of the pipe's instances. A caller either takes a place only when one is free
/// (<see cref="TryEnter"/>) or waits its turn for one (<see cref="EnterAsync"/>); places are handed to those waiting in
/// the order they came.
/// </summary>
internal sealed class RequestCeiling(int limit)
{
   /// <summary>Guards the count and the queue.</summary>
   private readonly Lock _gate = new();



   /// <summary>Those waiting for a place, oldest first. One that stopped waiting stays here until its turn comes and is then passed over.</summary>
   private readonly Queue<TaskCompletionSource> _waiting = new();



   /// <summary>How many places are taken.</summary>
   private int _inFlight;



   /// <summary>Takes a place when one is free, and says whether it did. It never waits.</summary>
   internal bool TryEnter()
   {
      lock(_gate)
      {
         if(_inFlight >= limit)
         {
            return false;
         }
         _inFlight++;
         return true;
      }
   }



   /// <summary>
   /// Takes a place, waiting for one when none is free. The task is already complete when a place was free. Cancelling
   /// the token ends the wait without a place, as a cancelled task; a place that had already been handed over stays
   /// taken, and the caller must give it back.
   /// </summary>
   internal Task EnterAsync(CancellationToken cancellationToken)
   {
      TaskCompletionSource turn;
      lock(_gate)
      {
         if(_inFlight < limit)
         {
            _inFlight++;
            return Task.CompletedTask;
         }
         turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
         _waiting.Enqueue(turn);
      }
      return WaitAsync(turn, cancellationToken);
   }



   /// <summary>Gives a place back. It goes to the one that has waited longest and still waits; when nobody waits, it becomes free.</summary>
   internal void Leave()
   {
      lock(_gate)
      {
         while(_waiting.TryDequeue(out TaskCompletionSource? turn))
         {
            if(turn.TrySetResult())
            {
               return;
            }
         }
         _inFlight--;
      }
   }



   /// <summary>Waits for a turn, or until the token is cancelled, whichever comes first.</summary>
   private static async Task WaitAsync(TaskCompletionSource turn, CancellationToken cancellationToken)
   {
      using(cancellationToken.UnsafeRegister(static (state, token) => ((TaskCompletionSource)state!).TrySetCanceled(token), turn))
      {
         await turn.Task.ConfigureAwait(false);
      }
   }
}
