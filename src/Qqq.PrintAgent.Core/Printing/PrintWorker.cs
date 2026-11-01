/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>Serial happy-path worker. A locally ambiguous submission is never automatically printed again.</summary>
public sealed class PrintWorker(PrintApiClient api, IPrinterBackend printers, IJobJournal journal, Action<string>? log = null,
   Func<bool>? canDispatch = null, Func<bool>? canSynchronize = null, PrintWorkerOptions? options = null)
{
   public async Task<bool> RunOnceAsync(CancellationToken token = default)
   {
      // A recovered job must be reported even when the server never claims it again.
      foreach((string jobId, string previousState) in options?.RecoverIncomplete == false ? [] : journal.Incomplete())
      {
         Record(jobId, previousState is "SUBMITTING" ? "OUTCOME_UNKNOWN" : "FAILED",
            previousState is "SUBMITTING" ? "Agent restarted during submission; inspect the printer before reprinting." : "Agent stopped before submitting this job; submit a new job if printing is still needed.");
      }
      await FlushAsync(token).ConfigureAwait(false);
      if(canSynchronize?.Invoke() == false)
      {
         return false;
      }
      if(!api.UsesRealtime)
      { await api.HeartbeatAsync(token).ConfigureAwait(false); }
      if(journal.Pending().Count > 0)
      {
         // Drain a bounded outbox batch before claiming more work after an outage.
         return false;
      }
      long inventoryStarted = System.Diagnostics.Stopwatch.GetTimestamp();
      IReadOnlyList<PrinterInfo> inventory = printers.Discover();
      TimeSpan inventoryElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(inventoryStarted);
      if(canSynchronize?.Invoke() == false)
      {
         return false;
      }
      if(options?.SynchronizeInventory != false)
      { await api.InventoryAsync(inventory, token).ConfigureAwait(false); }
      if(canDispatch?.Invoke() == false)
      {
         return false;
      }
      long claimStarted = System.Diagnostics.Stopwatch.GetTimestamp();
      PrintJob? job = await ClaimAndRegisterAsync(inventory, token).ConfigureAwait(false);
      TimeSpan claimElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(claimStarted);
      if(job is null)
      {
         return false;
      }
      try
      { return await ProcessJobAsync(job, inventory, new JobAttempt(inventoryElapsed, claimElapsed), token).ConfigureAwait(false); }
      finally { options?.ActiveJobChanged?.Invoke(null); }
   }

   private async Task<PrintJob?> ClaimAndRegisterAsync(IReadOnlyList<PrinterInfo> inventory, CancellationToken token)
   {
      if(options?.ClaimGate is not null)
      { await options.ClaimGate.WaitAsync(token).ConfigureAwait(false); }
      try
      {
         if(canDispatch?.Invoke() == false || options?.CanClaim?.Invoke() == false)
         { return null; }
         PrintJob? job = await api.ClaimAsync(token).ConfigureAwait(false);
         if(job is not null)
         {
            PrinterInfo? printer = inventory.FirstOrDefault(item => string.Equals(item.PrinterId, job.PrinterId, StringComparison.Ordinal));
            journal.TrackJob(job, string.IsNullOrWhiteSpace(printer?.DisplayName) ? printer?.Name : printer.DisplayName);
            if(journal.GetState(job.JobId) is "SPOOLED" or "SPOOLER_COMPLETED" or "FAILED" or "OUTCOME_UNKNOWN")
            { return null; }
            options?.ActiveJobChanged?.Invoke(job);
            try
            {
               Record(job, "RECEIVED");
               // The server replays CLAIMED until RECEIVED commits. Serialize admission through its ACK.
               await FlushAsync(token).ConfigureAwait(false);
            }
            catch
            {
               Record(job, "FAILED", "Receipt acknowledgement was interrupted before document submission; no bytes were submitted.");
               options?.ActiveJobChanged?.Invoke(null);
               throw;
            }
         }
         return job;
      }
      finally { options?.ClaimGate?.Release(); }
   }

   /// <summary>
   /// Runs one claimed job and reports its outcome. The submission steps live in <see cref="SubmitJobAsync"/> so their
   /// disposal scope stays inside this method's try block, which keeps every failure, including one raised while
   /// releasing a reservation, recorded through the same FAILED or OUTCOME_UNKNOWN decision. One timing line per job is
   /// logged afterwards so a slow print can be attributed to a phase from the service's own log without any extra
   /// request, payload or printer access.
   /// </summary>
   private async Task<bool> ProcessJobAsync(PrintJob job, IReadOnlyList<PrinterInfo> inventory, JobAttempt attempt, CancellationToken token)
   {
      PrinterInfo? assignedPrinter = inventory.FirstOrDefault(printer => string.Equals(printer.PrinterId, job.PrinterId, StringComparison.Ordinal));
      journal.TrackJob(job, string.IsNullOrWhiteSpace(assignedPrinter?.DisplayName) ? assignedPrinter?.Name : assignedPrinter.DisplayName);

      string? state = journal.GetState(job.JobId);
      if(state is "SPOOLED" or "SPOOLER_COMPLETED" or "FAILED" or "OUTCOME_UNKNOWN")
      {
         return false;
      }

      attempt.Restart();
      try
      { await SubmitJobAsync(job, attempt, token).ConfigureAwait(false); }
      catch(Exception exception)
      {
         Record(job, attempt.Submitting && exception is not PrinterMediaMismatchException ? "OUTCOME_UNKNOWN" : "FAILED", exception.Message);
      }
      log?.Invoke(attempt.TimingMessage(job.JobId));
      if(!token.IsCancellationRequested)
      { await FlushAsync(token).ConfigureAwait(false); }
      return attempt.Spooled || options?.RecoverIncomplete == false;
   }

   /// <summary>
   /// Downloads, revalidates and submits one job. The reservation, staged file and submission lease are declared here and
   /// released when this method returns, after SPOOLED is recorded, exactly as when the same steps ran inline. The
   /// attempt records how far the job got so the caller can tell a refusal before any bytes from an uncertain submission.
   /// </summary>
   private async Task SubmitJobAsync(PrintJob job, JobAttempt attempt, CancellationToken token)
   {
      if(job.Options.Copies < 1 || job.Options.Copies > EffectiveSettings.MaxCopies)
      {
         throw new InvalidDataException("Copies exceed the effective printing policy.");
      }
      await using IAsyncDisposable? reservation = options?.ReserveDocumentAsync is null ? null : await options.ReserveDocumentAsync(job, token).ConfigureAwait(false);
      using DownloadedDocument? file = options?.StagingDirectory is null ? null : await api.DownloadFileAsync(job, options.StagingDirectory, options.Settings, token, options.ThrottleDownloadAsync).ConfigureAwait(false);
      byte[]? content = file is null ? await api.DownloadAsync(job, token).ConfigureAwait(false) : null;
      attempt.Download = attempt.Lap();
      RequireCurrentPrinter(job);
      attempt.Revalidate = attempt.Lap();

      await using IAsyncDisposable? submission = options?.AcquireSubmissionAsync is null ? null : await options.AcquireSubmissionAsync(job, token).ConfigureAwait(false);
      RequireSubmitAllowed("Dispatch is paused before native submission.");
      Record(job, "SUBMITTING");
      await FlushAsync(token).ConfigureAwait(false);
      attempt.Gate = attempt.Lap();
      RequireSubmitAllowed("Dispatch stopped before native submission; no bytes were submitted.");
      RealtimeSettings submissionSettings = EffectiveSettings;
      if(job.Options.Copies > submissionSettings.MaxCopies || job.ContentLength > submissionSettings.MaximumDocumentBytes(job.Format))
      { throw new InvalidDataException("Document or copies exceed the current submission policy."); }
      token.ThrowIfCancellationRequested();
      attempt.Submitting = true;
      attempt.Restart();
      string spoolId = await SubmitNativeAsync(job, file, content, submissionSettings, token).ConfigureAwait(false);
      attempt.Submit = attempt.Lap();
      Record(job, "SPOOLED", spoolerJobId: spoolId);
      attempt.Spooled = true;
   }

   /// <summary>
   /// Discovers the printers again after the download and requires the assigned queue to still offer the job's format and
   /// the selected media profile, so a queue or driver default that changed while the document streamed is refused before
   /// any submission instead of printing on the wrong stock.
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
      if((options?.CanSubmit ?? canDispatch)?.Invoke() == false)
      { throw new InvalidOperationException(refusal); }
   }

   /// <summary>
   /// Hands the verified staged file, or the in-memory content for a backend without file support, to the native printer
   /// and returns its spooler receipt. This is the only step after which an outcome is uncertain.
   /// </summary>
   private Task<string> SubmitNativeAsync(PrintJob job, DownloadedDocument? file, byte[]? content, RealtimeSettings submissionSettings, CancellationToken token)
   {
      return file is not null && printers is IFilePrinterBackend files
         ? files.PrintFileAsync(job, file.Path, TimeSpan.FromMilliseconds(submissionSettings.NativeSubmissionTimeoutMillis), token)
         : printers.PrintAsync(job, content ?? throw new InvalidOperationException("File printing is required."), token);
   }

   private RealtimeSettings EffectiveSettings => options?.CurrentSettings?.Invoke() ?? options?.Settings ?? new();

   private void Record(PrintJob job, string state, string? message = null, string? spoolerJobId = null)
   {
      Record(job.JobId, state, message, spoolerJobId);
   }

   private void Record(string jobId, string state, string? message = null, string? spoolerJobId = null)
   {
      journal.Record(jobId, new PrintEvent(Guid.NewGuid().ToString("N"), state, message, spoolerJobId));
      log?.Invoke($"{jobId}: {state}");
   }

   private async Task FlushAsync(CancellationToken token)
   {
      if(options?.EventGate is not null)
      { await options.EventGate.WaitAsync(token).ConfigureAwait(false); }
      try
      {
         foreach((string jobId, PrintEvent printEvent) in journal.Pending())
         {
            await api.EventAsync(jobId, printEvent, token).ConfigureAwait(false);
            journal.Acknowledge(printEvent.EventId);
         }
      }
      finally { options?.EventGate?.Release(); }
   }

   /// <summary>
   /// Progress and phase timing of one claimed job. Progress tells the caller whether native submission began, which
   /// decides between a known refusal and an uncertain outcome; the durations come from the monotonic clock and feed the
   /// single timing line, so they carry no document content.
   /// </summary>
   private sealed class JobAttempt(TimeSpan inventory, TimeSpan claim)
   {
      private long _mark = System.Diagnostics.Stopwatch.GetTimestamp();

      public bool Submitting { get; set; }

      public bool Spooled { get; set; }

      public TimeSpan Download { get; set; }

      public TimeSpan Revalidate { get; set; }

      public TimeSpan Gate { get; set; }

      public TimeSpan Submit { get; set; }

      /// <summary>Starts the next phase now, discarding the time since the previous mark.</summary>
      public void Restart()
      {
         _mark = System.Diagnostics.Stopwatch.GetTimestamp();
      }

      /// <summary>Returns the time since the previous mark and starts the next phase at the same instant, so phases never overlap or leave a gap.</summary>
      public TimeSpan Lap()
      {
         long now = System.Diagnostics.Stopwatch.GetTimestamp();
         TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_mark, now);
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
