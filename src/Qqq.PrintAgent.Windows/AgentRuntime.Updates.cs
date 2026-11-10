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
   /// not stopped by the pause: it finishes and is reported. The pause is set under the connection gate, which orders it against
   /// a sign-in or a state change in progress, and the gate is released before the wait: a desired state or settings the
   /// server sends meanwhile must still be saved and acknowledged, or the runtime would stay not ready and the very jobs
   /// being waited for could not be submitted. When the wait takes longer than the preparation limit the answer says what was
   /// still in progress, the pause stays, and that work carries on. Because the gate is not held, other commands can run
   /// during the wait, so the answer is decided afresh when the wait ends.
   /// </summary>
   internal async Task<string> PrepareUpdateAsync(CancellationToken token)
   {
      await _connectionGate.WaitAsync(token).ConfigureAwait(false);
      try
      {
         lock(_stateLock)
         {
            _updatePauseUntil = ObservationTime + UpdatePauseDuration;
         }
      }
      finally
      {
         _connectionGate.Release();
      }
      using var limit = new CancellationTokenSource(UpdatePreparationLimit, Clock);
      using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token, limit.Token);
      bool claimAnswered = false;
      try
      {
         // A claim already on its way to the server holds the claim gate until its job is registered, so passing the
         // gate once means every such claim has been answered and its job, if any, has a ticket.
         await _claimGate.WaitAsync(bounded.Token).ConfigureAwait(false);
         _claimGate.Release();
         claimAnswered = true;
         await WaitForClaimedWorkAsync(bounded.Token).ConfigureAwait(false);
      }
      catch(OperationCanceledException) when(limit.IsCancellationRequested && !token.IsCancellationRequested)
      {
         if(PreparationLimitReached(claimAnswered) is string notReady)
         { return notReady; }
      }
      return PreparationOutcome();
   }



   /// <summary>
   /// Decides the answer once nothing claimed before the pause is in progress. "Ready for update." tells the updater that
   /// files may be replaced, which is true only while the pause is still in force and the connection is not being changed.
   /// Another command may have resumed printing during the wait (claims may then have started again), the pause may have run
   /// out, or a sign-in, sign-out or enrollment may be under way; in each of those cases the answer says that preparation
   /// was interrupted and must be repeated.
   /// </summary>
   private string PreparationOutcome()
   {
      bool paused;
      bool transitioning;
      lock(_stateLock)
      {
         paused = _updatePauseUntil > ObservationTime;
         transitioning = _transitioning;
      }
      if(paused && !transitioning)
      {
         _log.Write("INFO", "Printing paused for an application update after active submission finished.");
         return "Ready for update.";
      }
      string why = paused ? "a sign-in, sign-out or enrollment began while preparation was waiting" : "printing was resumed, or the pause ran out, while preparation was waiting";
      _log.Write("WARN", $"Update preparation was interrupted: {why}. The service is not ready for an update; the updater must prepare again.");
      return $"Update preparation was interrupted: {why}. Try the update again.";
   }



   /// <summary>
   /// Says what was still in progress when the preparation limit ran out: jobs claimed before the pause, a claim the server
   /// had not answered yet, or both. It logs that and returns the reply for the updater. It returns null when the claim was
   /// answered and the last job finished at the same moment the limit ran out, because the service is then ready after all.
   /// </summary>
   private string? PreparationLimitReached(bool claimAnswered)
   {
      int remaining;
      lock(_stateLock)
      { remaining = _jobTickets.Count; }
      if(claimAnswered && remaining == 0)
      { return null; }
      string seconds = UpdatePreparationLimit.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
      (string what, string outcome) = (claimAnswered, remaining) switch
      {
         (true, _) => ($"{remaining} job(s) were", "They will finish;"),
         (false, 0) => ("a claim was", "A job it returns will be printed;"),
         _ => ($"a claim and {remaining} job(s) were", "The jobs will finish, and a job the claim returns will be printed;"),
      };
      _log.Write("WARN", $"Update preparation stopped waiting after {seconds} s: {what} still in progress from before the pause. {outcome} new claims stay paused until the updater resumes printing or the pause runs out.");
      return $"Not ready for update: {what} still in progress after {seconds} seconds. {outcome} try the update again afterwards.";
   }



   /// <summary>Waits until no job claimed before the pause remains: for the last ticket to be released, on the pipeline signal rather than a timer.</summary>
   private async Task WaitForClaimedWorkAsync(CancellationToken token)
   {
      while(true)
      {
         Task changed;
         lock(_stateLock)
         {
            if(_jobTickets.Count == 0)
            { return; }
            changed = _pipelineChanged.NextChange;
         }
         await changed.WaitAsync(token).ConfigureAwait(false);
      }
   }



   /// <summary>Lifts the update pause, wakes the claim loop and logs it; called by the update helper after the installer finishes, whatever its result.</summary>
   internal string ResumeAfterUpdate()
   {
      lock(_stateLock)
      {
         _updatePauseUntil = default;
      }
      WakeClaims();
      _pipelineChanged.Raise();
      _log.Write("INFO", "Printing resumed after the application update attempt.");
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
      _log.Write("WARN", "The update pause ran out without the updater resuming printing; printing continues.");
      WakeClaims();
      _pipelineChanged.Raise();
   }
}
