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
      IReadOnlyList<PrinterInfo> inventory = printers.Discover();
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
      PrintJob? job = await ClaimAndRegisterAsync(inventory, token).ConfigureAwait(false);
      if(job is null)
      {
         return false;
      }
      try
      { return await ProcessJobAsync(job, inventory, token).ConfigureAwait(false); }
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

   private async Task<bool> ProcessJobAsync(PrintJob job, IReadOnlyList<PrinterInfo> inventory, CancellationToken token)
   {
      PrinterInfo? assignedPrinter = inventory.FirstOrDefault(printer => string.Equals(printer.PrinterId, job.PrinterId, StringComparison.Ordinal));
      journal.TrackJob(job, string.IsNullOrWhiteSpace(assignedPrinter?.DisplayName) ? assignedPrinter?.Name : assignedPrinter.DisplayName);

      string? state = journal.GetState(job.JobId);
      if(state is "SPOOLED" or "SPOOLER_COMPLETED" or "FAILED" or "OUTCOME_UNKNOWN")
      {
         return false;
      }

      bool submitting = false;
      bool spooled = false;
      try
      {
         if(job.Options.Copies < 1 || job.Options.Copies > EffectiveSettings.MaxCopies)
         {
            throw new InvalidDataException("Copies exceed the effective printing policy.");
         }
         await using IAsyncDisposable? reservation = options?.ReserveDocumentAsync is null ? null : await options.ReserveDocumentAsync(job, token).ConfigureAwait(false);
         using DownloadedDocument? file = options?.StagingDirectory is null ? null : await api.DownloadFileAsync(job, options.StagingDirectory, options.Settings, token, options.ThrottleDownloadAsync).ConfigureAwait(false);
         byte[]? content = file is null ? await api.DownloadAsync(job, token).ConfigureAwait(false) : null;
         PrinterInfo? currentPrinter = printers.Discover().SingleOrDefault(p => string.Equals(p.PrinterId, job.PrinterId, StringComparison.Ordinal) && p.Formats.Contains(job.Format, StringComparer.Ordinal));
         if(currentPrinter is null)
         {
            throw new InvalidOperationException("Assigned printer or document format is unavailable.");
         }
         PrinterMediaProfile.RequireMatch(job.Options, currentPrinter.MediaProfiles);

         await using IAsyncDisposable? submission = options?.AcquireSubmissionAsync is null ? null : await options.AcquireSubmissionAsync(job, token).ConfigureAwait(false);
         if((options?.CanSubmit ?? canDispatch)?.Invoke() == false)
         { throw new InvalidOperationException("Dispatch is paused before native submission."); }
         Record(job, "SUBMITTING");
         await FlushAsync(token).ConfigureAwait(false);
         if((options?.CanSubmit ?? canDispatch)?.Invoke() == false)
         { throw new InvalidOperationException("Dispatch stopped before native submission; no bytes were submitted."); }
         RealtimeSettings submissionSettings = EffectiveSettings;
         if(job.Options.Copies > submissionSettings.MaxCopies || job.ContentLength > submissionSettings.MaximumDocumentBytes(job.Format))
         { throw new InvalidDataException("Document or copies exceed the current submission policy."); }
         token.ThrowIfCancellationRequested();
         submitting = true;
         string spoolId = file is not null && printers is IFilePrinterBackend files
            ? await files.PrintFileAsync(job, file.Path, TimeSpan.FromMilliseconds(submissionSettings.NativeSubmissionTimeoutMillis), token).ConfigureAwait(false)
            : await printers.PrintAsync(job, content ?? throw new InvalidOperationException("File printing is required."), token).ConfigureAwait(false);
         Record(job, "SPOOLED", spoolerJobId: spoolId);
         spooled = true;
      }
      catch(Exception exception)
      {
         Record(job, submitting && exception is not PrinterMediaMismatchException ? "OUTCOME_UNKNOWN" : "FAILED", exception.Message);
      }
      if(!token.IsCancellationRequested)
      { await FlushAsync(token).ConfigureAwait(false); }
      return spooled || options?.RecoverIncomplete == false;
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
}



