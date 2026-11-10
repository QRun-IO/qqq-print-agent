/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Update maintenance part of the runtime: a bounded pause of new claims so the installer can replace files while no job is in
/// progress, and the resume that lifts it. Work claimed before the pause is finished, not abandoned. The pause expires by
/// itself, so an abandoned update can never leave printing off.
/// </summary>
public sealed partial class AgentRuntime
{
   /// <summary>How long a pause lasts when the updater never resumes printing.</summary>
   internal static readonly TimeSpan UpdatePauseDuration = TimeSpan.FromMinutes(10);

   /// <summary>How long update preparation waits for claimed work before it answers that the service is not ready. It is shorter than the pipe's 30-second command limit, so the answer still reaches the updater.</summary>
   internal static readonly TimeSpan UpdatePreparationLimit = TimeSpan.FromSeconds(25);

   private DateTimeOffset _updatePauseUntil;



   /// <summary>True while an update pause is in force; checked under the state lock before any claim starts.</summary>
   private bool UpdatePaused
   {
      get
      {
         lock(_stateLock)
         {
            return _updatePauseUntil > ObservationTime;
         }
      }
   }



   /// <summary>
   /// Starts the pause and answers "Ready for update." only when nothing claimed before it is still in progress, so the
   /// installer never replaces files under a job that is downloading, waiting for its printer or being submitted. Such a job is
   /// not stopped by the pause: it finishes and is reported. When that takes longer than the preparation limit the answer says
   /// how many jobs were still in progress, the pause stays, and the jobs carry on.
   /// </summary>
   private async Task<string> PrepareUpdateAsync(CancellationToken token)
   {
      await _connectionGate.WaitAsync(token).ConfigureAwait(false);
      try
      {
         lock(_stateLock)
         {
            _updatePauseUntil = ObservationTime + UpdatePauseDuration;
         }
         using var limit = new CancellationTokenSource(UpdatePreparationLimit, Clock);
         using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token, limit.Token);
         try
         {
            await WaitForClaimedWorkAsync(bounded.Token).ConfigureAwait(false);
         }
         catch(OperationCanceledException) when(limit.IsCancellationRequested && !token.IsCancellationRequested)
         {
            int remaining;
            lock(_stateLock)
            { remaining = _jobTickets.Count; }
            string seconds = UpdatePreparationLimit.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
            WriteLog("WARN", $"Update preparation stopped waiting after {seconds} s: {remaining} job(s) claimed before the pause were still in progress. They continue; new claims stay paused until the updater resumes printing or the pause runs out.");
            return $"Not ready for update: {remaining} job(s) were still in progress after {seconds} seconds. They will finish; try the update again afterwards.";
         }
         WriteLog("INFO", "Printing paused for an application update after active submission finished.");
         return "Ready for update.";
      }
      finally
      {
         _connectionGate.Release();
      }
   }



   /// <summary>
   /// Waits until no job claimed before the pause remains. A claim that was already on its way to the server holds the claim
   /// gate until its job is registered, so passing that gate once means every such claim has a ticket; after that the wait is
   /// for the last ticket to be released, on the pipeline signal rather than a timer.
   /// </summary>
   private async Task WaitForClaimedWorkAsync(CancellationToken token)
   {
      await _claimGate.WaitAsync(token).ConfigureAwait(false);
      _claimGate.Release();
      while(true)
      {
         Task changed;
         lock(_stateLock)
         {
            if(_jobTickets.Count == 0)
            { return; }
            changed = _pipelineChanged.Task;
         }
         await changed.WaitAsync(token).ConfigureAwait(false);
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



   /// <summary>
   /// Notices that a pause ran out without a resume. Nothing else would start a claim drain at that moment, so jobs the server
   /// announced during the pause would wait for its next announcement; the heartbeat calls this each round and the claim loop
   /// is woken once.
   /// </summary>
   private void ResumeWhenUpdatePauseRanOut()
   {
      lock(_stateLock)
      {
         if(_updatePauseUntil == default || _updatePauseUntil > ObservationTime)
         { return; }
         _updatePauseUntil = default;
      }
      WriteLog("WARN", "The update pause ran out without the updater resuming printing; printing continues.");
      WakePoll();
      SignalPipeline();
   }
}
