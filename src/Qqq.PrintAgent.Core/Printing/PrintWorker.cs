/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// One lane of the print pipeline: each pass claims at most one job, stages its document and submits it once. A
/// locally ambiguous submission is never automatically printed again. The worker keeps those rules and nothing else:
/// whether it may dispatch, claim or submit, the limits in force, the gates shared with the other lanes and the
/// leases a job holds all come from the runtime that composes it, and every one of them is required. Jobs a previous
/// run left unfinished are reported by the runtime once, at start, and inventory and heartbeats are the runtime's to
/// send, so a pass does none of that.
/// </summary>
public sealed class PrintWorker(PrintApiClient api, IPrinterBackend printers, IJobJournal journal, Action<string> log,
   Func<bool> canDispatch, Func<bool> canSynchronize, PrintWorkerOptions options)
{
   /// <summary>
   /// One pass of a lane. It first drains the durable event outbox, because an outcome the server never heard of would
   /// stay invisible. Only when nothing is pending, the connection is synchronized and dispatch is allowed does it
   /// discover printers and claim at most one job. Returns true whenever a claimed job was processed, whatever its
   /// outcome, and when a malformed job was refused and reported, so the lane keeps draining; false when idle,
   /// paused, blocked, or the claimed job was one the journal already holds as final or as submitting, which ends the
   /// lane's pass because the server would only offer that job again.
   /// </summary>
   public async Task<bool> RunOnceAsync(CancellationToken token)
   {
      await FlushAsync(token).ConfigureAwait(false);
      if(!canSynchronize())
      {
         return false;
      }
      if(journal.Pending().Count > 0)
      {
         // Drain a bounded outbox batch before claiming more work after an outage.
         return false;
      }
      long inventoryStarted = Time.GetTimestamp();
      IReadOnlyList<PrinterInfo> inventory = printers.Discover();
      TimeSpan inventoryElapsed = Time.GetElapsedTime(inventoryStarted);
      if(!canSynchronize() || !canDispatch())
      {
         return false;
      }
      long claimStarted = Time.GetTimestamp();
      Claim claim = await ClaimAndRegisterAsync(inventory, token).ConfigureAwait(false);
      TimeSpan claimElapsed = Time.GetElapsedTime(claimStarted);
      if(claim.Job is null)
      {
         // A refused job was reported as FAILED, so the lane keeps draining exactly as it does after any other failed job.
         return claim.Refused;
      }
      try
      { return await ProcessJobAsync(claim.Job, inventory, new JobAttempt(Time, inventoryElapsed, claimElapsed), token).ConfigureAwait(false); }
      finally { options.ActiveJobChanged(null); }
   }



   /// <summary>
   /// Claims one job under the shared claim gate and registers it locally before anything else can happen to it. The
   /// server keeps replaying CLAIMED until RECEIVED commits, so admission is serialized through that acknowledgement; if
   /// it is interrupted the job is recorded FAILED with no bytes submitted. A job this computer is already working on,
   /// or that the journal holds in a final state or as submitting, is dropped here so a replayed claim can never print
   /// twice; the pass then ends, because the server would only offer that job again. A claimed job that breaks the
   /// contract but names itself is refused and reported instead of returned.
   /// </summary>
   private async Task<Claim> ClaimAndRegisterAsync(IReadOnlyList<PrinterInfo> inventory, CancellationToken token)
   {
      await options.ClaimGate.WaitAsync(token).ConfigureAwait(false);
      try
      {
         if(!canDispatch() || !options.CanClaim())
         { return default; }
         PrintJob? job;
         try
         { job = await api.ClaimAsync(token).ConfigureAwait(false); }
         catch(PrintingProtocolException violation) when(violation.RejectedJob is not null)
         { return new Claim(null, await RefuseAsync(violation.RejectedJob, violation.Message, inventory, token).ConfigureAwait(false)); }
         if(job is null || IsReplay(job, inventory))
         { return default; }
         options.ActiveJobChanged(job);
         try
         {
            Record(job, PrintJobState.Received);
            // The server replays CLAIMED until RECEIVED commits. Serialize admission through its ACK.
            await FlushAsync(token).ConfigureAwait(false);
         }
         catch
         {
            // The lane lets go of the job whatever the recording does: a ticket left behind would make every later offer of the job read as one a lane still holds.
            try
            { Record(job, PrintJobState.Failed, "Receipt acknowledgement was interrupted before document submission; no bytes were submitted."); }
            finally { options.ActiveJobChanged(null); }
            throw;
         }
         return new Claim(job, false);
      }
      finally { options.ClaimGate.Release(); }
   }



   /// <summary>
   /// Says whether a claimed job is one this computer must not start on, because it already has, and says so in the
   /// log when the server should not have offered it. A job that is in hand, held by a lane of this computer right
   /// now, is left entirely to that lane: nothing is recorded for it here, not even its display facts. The lane lets
   /// go of a job only after its final state was recorded and delivered, so a job in hand may be one that has just
   /// finished, and the log line says "still holds" for that reason and does not claim work is under way. A job the
   /// journal holds as submitting was handed to a printer once already and has not concluded, and one it holds in a
   /// final state has; neither is ever submitted a second time.
   /// </summary>
   private bool IsReplay(PrintJob job, IReadOnlyList<PrinterInfo> inventory)
   {
      if(options.IsInHand(job.JobId))
      {
         log($"{job.JobId}: offered again while a lane of this computer still holds it, working on it or just finished with it; it is left to that lane, nothing is recorded for it and this pass claims nothing more.");
         return true;
      }
      Track(job, inventory);
      if(journal.GetState(job.JobId) is not { } state || !(PrintJobState.IsFinal(state) || string.Equals(state, PrintJobState.Submitting, StringComparison.Ordinal)))
      { return false; }

      ////////////////////////////////////////////////////////////////////////
      // a job recorded as submitting was handed to a printer once already  //
      // and has not concluded: another lane is submitting it now, or a run //
      // stopped part-way and the runtime's recovery reports it as unknown. //
      // claiming it again must never print it a second time, and it must   //
      // not pass silently, because the server should not be offering it    //
      ////////////////////////////////////////////////////////////////////////
      if(!PrintJobState.IsFinal(state))
      { log($"{job.JobId}: offered again while recorded as {state}, so its submission has started and not concluded; it is not submitted a second time and this pass claims nothing more."); }
      return true;
   }



   /// <summary>
   /// Reports a claimed job the API client refused because it broke the contract. The server offers a claimed job again
   /// until the agent answers for it, so leaving it unanswered would block every job behind it; the job is therefore
   /// recorded as received and then FAILED, with the reason and the fact that nothing was submitted, and both events are
   /// delivered. A job the journal already knows, in any state, is left exactly as it is: it was answered for once, its
   /// outcome is recorded or another lane is working on it right now, and a FAILED written over RECEIVED or SUBMITTING
   /// would claim that nothing was submitted while a submission may be under way. The replay is only logged. Returns
   /// true when the refusal was reported.
   /// </summary>
   private async Task<bool> RefuseAsync(PrintJob rejected, string reason, IReadOnlyList<PrinterInfo> inventory, CancellationToken token)
   {
      if(journal.GetState(rejected.JobId) is { } known)
      {
         log($"{rejected.JobId}: offered again in a form that breaks the contract while already recorded as {known}; nothing was recorded or reported for it. {reason}");
         return false;
      }
      Track(rejected, inventory);
      Record(rejected, PrintJobState.Received);
      Record(rejected, PrintJobState.Failed, reason + " No bytes were submitted.");
      await FlushAsync(token).ConfigureAwait(false);
      return true;
   }



   /// <summary>Stores the job's display facts in the journal, naming its printer by the operator's friendly name when one is set and by the queue name otherwise.</summary>
   private void Track(PrintJob job, IReadOnlyList<PrinterInfo> inventory)
   {
      PrinterInfo? printer = inventory.FirstOrDefault(item => string.Equals(item.PrinterId, job.PrinterId, StringComparison.Ordinal));
      journal.TrackJob(job, string.IsNullOrWhiteSpace(printer?.DisplayName) ? printer?.Name : printer.DisplayName);
   }



   /// <summary>The result of one claim: the job to process, or none, and whether a malformed job was refused and reported instead.</summary>
   private readonly record struct Claim(PrintJob? Job, bool Refused);



   /// <summary>
   /// Runs one claimed job and reports its outcome. The submission steps live in <see cref="SubmitJobAsync"/> so their
   /// disposal scope stays inside this method's try block, which keeps every failure, including one raised while
   /// releasing a reservation, inside the same decision: FAILED when Windows was never asked, OUTCOME_UNKNOWN when
   /// submission had begun, and nothing at all when the job is already recorded as SPOOLED, because a release that
   /// fails after Windows accepted the document says nothing about the print and must not be written over it. Two
   /// refusals that arrive after submission began are still FAILED, because each is known to have been made before
   /// any bytes were sent: the native layer's media mismatch, and the exposure filter's refusal of a printer that is
   /// no longer shared, which only the filter can raise and raises before it calls the backend behind it. One
   /// timing line per job is logged afterwards so a slow print can be attributed to a phase from the service's own log
   /// without any extra request, payload or printer access. Returns true when the job was processed, whatever its
   /// outcome; false only when something else had already recorded a final state for it, so nothing was done.
   /// </summary>
   private async Task<bool> ProcessJobAsync(PrintJob job, IReadOnlyList<PrinterInfo> inventory, JobAttempt attempt, CancellationToken token)
   {
      Track(job, inventory);
      if(PrintJobState.IsFinal(journal.GetState(job.JobId)))
      {
         return false;
      }

      attempt.Restart();
      try
      { await SubmitJobAsync(job, attempt, token).ConfigureAwait(false); }
      catch(Exception exception)
      {
         if(attempt.Spooled)
         { log($"{job.JobId}: spooled, and stays recorded as spooled; releasing what the job held failed afterwards: {exception.Message}"); }
         else
         { Record(job, attempt.Submitting && exception is not (PrinterMediaMismatchException or PrinterNotExposedException) ? PrintJobState.OutcomeUnknown : PrintJobState.Failed, exception.Message); }
      }
      log(attempt.TimingMessage(job.JobId));
      if(!token.IsCancellationRequested)
      { await FlushAsync(token).ConfigureAwait(false); }
      return true;
   }



   /// <summary>
   /// Stages, revalidates and submits one job. The reservation, staged file and submission lease are declared here and
   /// released when this method returns, after SPOOLED is recorded. The attempt records how far the job got so the
   /// caller can tell a refusal before any bytes from an uncertain submission. Between recording SUBMITTING and calling
   /// Windows nothing has been sent, so the two things that can stop the job there (delivery of the receipt not being
   /// confirmed, and cancellation) say in their own words that no bytes were submitted. Handing the verified staged
   /// file to the backend is the only step after which an outcome is uncertain. The printer is discovered again once,
   /// after the download, and not a second time when its turn is held: that would start the print helper once more
   /// for every job, while the job holds the printer's turn and a print slot. What changes during the wait for the
   /// turn is refused later by whoever can tell that nothing was sent: the exposure filter for a printer that is no
   /// longer shared, and the native layer for media that changed.
   /// </summary>
   private async Task SubmitJobAsync(PrintJob job, JobAttempt attempt, CancellationToken token)
   {
      if(job.Options.Copies < 1 || job.Options.Copies > EffectiveSettings.MaxCopies)
      {
         throw new InvalidDataException("Copies exceed the effective printing policy.");
      }
      await using IAsyncDisposable reservation = await options.ReserveDocumentAsync(job, token).ConfigureAwait(false);
      using DownloadedDocument file = await api.DownloadFileAsync(job, options.StagingDirectory, options.Settings, options.ThrottleDownloadAsync, token).ConfigureAwait(false);
      attempt.Download = attempt.Lap();
      RequireCurrentPrinter(job);
      attempt.Revalidate = attempt.Lap();

      await using IAsyncDisposable submission = await options.AcquireSubmissionAsync(job, token).ConfigureAwait(false);
      RequireSubmitAllowed("Dispatch is paused before native submission.");
      Record(job, PrintJobState.Submitting);
      try
      { await FlushAsync(token).ConfigureAwait(false); }
      catch(Exception exception)
      {
         ///////////////////////////////////////////////////////////////////////////
         // SUBMITTING is on record but Windows has not been asked, so this is a  //
         // plain refusal. the flush sends every pending event, so it may have    //
         // failed on another job's event, on a lost acknowledgement or on the    //
         // journal; all that is known is that delivery of this receipt was not   //
         // confirmed. the failure's own message ("connection reset") would leave //
         // an operator who reads SUBMITTING then FAILED unsure whether anything  //
         // printed, so the refusal states that nothing was                       //
         ///////////////////////////////////////////////////////////////////////////
         throw new IOException("Delivery of the submitting receipt could not be confirmed, so the document was not sent to the printer; no bytes were submitted.", exception);
      }
      attempt.Gate = attempt.Lap();
      RequireSubmitAllowed("Dispatch stopped before native submission; no bytes were submitted.");
      RealtimeSettings submissionSettings = EffectiveSettings;
      if(job.Options.Copies > submissionSettings.MaxCopies || job.ContentLength > submissionSettings.MaximumDocumentBytes(job.Format))
      { throw new InvalidDataException("Document or copies exceed the current submission policy."); }
      if(token.IsCancellationRequested)
      { throw new OperationCanceledException("Stopped before native submission; no bytes were submitted.", token); }
      attempt.Submitting = true;
      attempt.Restart();
      string spoolId = await printers.PrintFileAsync(job, file.Path, TimeSpan.FromMilliseconds(submissionSettings.NativeSubmissionTimeoutMillis), token).ConfigureAwait(false);
      attempt.Submit = attempt.Lap();
      Record(job, PrintJobState.Spooled, spoolerJobId: spoolId);
      attempt.Spooled = true;
   }



   /// <summary>
   /// Discovers the printers again after the download and requires the assigned queue to still be shared, to still
   /// offer the job's format and to still have the selected media profile, so a printer withdrawn, or a queue or driver
   /// default changed, while the document streamed is refused before SUBMITTING is recorded, with Windows never asked,
   /// instead of printing on the wrong stock.
   /// </summary>
   private void RequireCurrentPrinter(PrintJob job)
   {
      PrinterInfo? currentPrinter = printers.Discover().SingleOrDefault(p => string.Equals(p.PrinterId, job.PrinterId, StringComparison.Ordinal) && p.Formats.Contains(job.Format, StringComparer.Ordinal));
      if(currentPrinter is null)
      {
         throw new InvalidOperationException("Assigned printer or document format is unavailable.");
      }
      PrinterMediaProfile.RequireMatch(job.Options, currentPrinter.MediaProfiles);
   }



   /// <summary>
   /// Refuses the submission when dispatch is paused or stopped. It is checked before and after the SUBMITTING receipt is
   /// acknowledged, because a pause can arrive while that acknowledgement is in flight and no bytes may be sent after it.
   /// </summary>
   private void RequireSubmitAllowed(string refusal)
   {
      if(!options.CanSubmit())
      { throw new InvalidOperationException(refusal); }
   }



   /// <summary>The limits in force right now, read from the runtime each time, so an administrator's policy change reaches a job that is already past its first check.</summary>
   private RealtimeSettings EffectiveSettings => options.CurrentSettings();



   /// <summary>The clock the phase timings are measured on: the one supplied in the options, the system clock unless a test supplies its own.</summary>
   private TimeProvider Time => options.TimeProvider;



   /// <summary>Records a state transition for a claimed job, delegating to the identifier overload.</summary>
   private void Record(PrintJob job, string state, string? message = null, string? spoolerJobId = null)
   {
      Record(job.JobId, state, message, spoolerJobId);
   }



   /// <summary>
   /// Writes the event to the durable journal first and only then to the log. The journal row is what survives a crash
   /// and is later delivered to the server, so a state is never reported before it is recorded locally.
   /// </summary>
   private void Record(string jobId, string state, string? message = null, string? spoolerJobId = null)
   {
      journal.Record(jobId, new PrintEvent(Guid.NewGuid().ToString("N"), state, message, spoolerJobId));
      log($"{jobId}: {state}");
   }



   /// <summary>
   /// Delivers pending journal events to the server in order, acknowledging each locally only after the server accepted
   /// it, so a failed delivery leaves the event pending and is retried instead of lost. The shared event gate keeps
   /// concurrent lanes from sending the same event twice.
   /// </summary>
   private async Task FlushAsync(CancellationToken token)
   {
      await options.EventGate.WaitAsync(token).ConfigureAwait(false);
      try
      {
         foreach((string jobId, PrintEvent printEvent) in journal.Pending())
         {
            await api.EventAsync(jobId, printEvent, token).ConfigureAwait(false);
            journal.Acknowledge(printEvent.EventId);
         }
      }
      finally { options.EventGate.Release(); }
   }



   /// <summary>
   /// Progress and phase timing of one claimed job. Progress tells the caller whether native submission began, which
   /// decides between a known refusal and an uncertain outcome; the durations come from the worker's monotonic clock and
   /// feed the single timing line, so they carry no document content.
   /// </summary>
   private sealed class JobAttempt(TimeProvider time, TimeSpan inventory, TimeSpan claim)
   {
      private long _mark = time.GetTimestamp();

      public bool Submitting { get; set; }

      public bool Spooled { get; set; }

      public TimeSpan Download { get; set; }

      public TimeSpan Revalidate { get; set; }

      public TimeSpan Gate { get; set; }

      public TimeSpan Submit { get; set; }



      /// <summary>Starts the next phase now, discarding the time since the previous mark.</summary>
      public void Restart()
      {
         _mark = time.GetTimestamp();
      }



      /// <summary>Returns the time since the previous mark and starts the next phase at the same instant, so phases never overlap or leave a gap.</summary>
      public TimeSpan Lap()
      {
         long now = time.GetTimestamp();
         TimeSpan elapsed = time.GetElapsedTime(_mark, now);
         _mark = now;
         return elapsed;
      }



      /// <summary>Formats the one per-job log line: the job identifier and whole milliseconds per phase, nothing else.</summary>
      public string TimingMessage(string jobId)
      {
         return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{jobId}: timing inventory={inventory.TotalMilliseconds:F0}ms claim={claim.TotalMilliseconds:F0}ms download={Download.TotalMilliseconds:F0}ms revalidate={Revalidate.TotalMilliseconds:F0}ms gate={Gate.TotalMilliseconds:F0}ms submit={Submit.TotalMilliseconds:F0}ms");
      }
   }
}
