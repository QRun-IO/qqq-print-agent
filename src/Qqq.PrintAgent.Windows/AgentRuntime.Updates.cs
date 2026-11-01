/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Update maintenance part of the runtime: a bounded printing pause so the installer can replace files while no job is being submitted,
/// and the resume that lifts it. The pause expires by itself, so an abandoned update can never leave printing off.
/// </summary>
public sealed partial class AgentRuntime
{
   private DateTimeOffset _updatePauseUntil;



   /// <summary>True while an update pause is in force; checked under the state lock before any claim or submission starts.</summary>
   private bool UpdatePaused
   {
      get
      {
         lock(_stateLock)
         {
            return _updatePauseUntil > DateTimeOffset.UtcNow;
         }
      }
   }



   /// <summary>Starts a ten minute printing pause and waits for submissions already in progress to finish, so the installer never replaces files under an active print; the caller is told the service is ready only after that.</summary>
   private async Task<string> PrepareUpdateAsync(CancellationToken token)
   {
      await _connectionGate.WaitAsync(token).ConfigureAwait(false);
      try
      {
         lock(_stateLock)
         {
            _updatePauseUntil = DateTimeOffset.UtcNow.AddMinutes(10);
         }
         while(true)
         {
            Task changed;
            lock(_stateLock)
            {
               if(_activePrints == 0)
               { break; }
               changed = _pipelineChanged.Task;
            }
            await changed.WaitAsync(token).ConfigureAwait(false);
         }
         WriteLog("INFO", "Printing paused for an application update after active submission finished.");
         return "Ready for update.";
      }
      finally
      {
         _connectionGate.Release();
      }
   }



   /// <summary>Lifts the update pause, wakes the claim loop and logs it; called by the update helper after the installer finishes, whatever its result.</summary>
   private string ResumeAfterUpdate()
   {
      lock(_stateLock)
      {
         _updatePauseUntil = default;
      }
      WakePoll();
      SignalPipeline();
      WriteLog("INFO", "Printing resumed after the application update attempt.");
      return "Printing resumed.";
   }
}
