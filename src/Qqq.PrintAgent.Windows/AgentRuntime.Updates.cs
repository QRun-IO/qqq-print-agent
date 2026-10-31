/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

public sealed partial class AgentRuntime
{
   private DateTimeOffset _updatePauseUntil;

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
