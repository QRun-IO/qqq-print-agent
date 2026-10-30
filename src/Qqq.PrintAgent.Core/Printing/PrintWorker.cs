/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>Serial happy-path worker. A locally ambiguous submission is never automatically printed again.</summary>
public sealed class PrintWorker(PrintApiClient api, IPrinterBackend printers, IJobJournal journal, Action<string>? log = null)
{
   public async Task RunOnceAsync(CancellationToken token = default)
   {
      // A recovered job must be reported even when the server never claims it again.
      foreach((string jobId, string previousState) in journal.Incomplete())
      {
         Record(jobId, previousState is "SUBMITTING" ? "OUTCOME_UNKNOWN" : "FAILED",
            previousState is "SUBMITTING" ? "Agent restarted during submission; inspect the printer before reprinting." : "Agent stopped before submitting this job; submit a new job if printing is still needed.");
      }
      await FlushAsync(token).ConfigureAwait(false);
      await api.HeartbeatAsync(token).ConfigureAwait(false);
      if(journal.Pending().Count > 0)
      {
         // Drain a bounded outbox batch before claiming more work after an outage.
         return;
      }
      IReadOnlyList<PrinterInfo> inventory = printers.Discover();
      await api.InventoryAsync(inventory, token).ConfigureAwait(false);
      PrintJob? job = await api.ClaimAsync(token).ConfigureAwait(false);
      if(job is null)
      {
         return;
      }

      journal.TrackJob(job, inventory.FirstOrDefault(printer => string.Equals(printer.PrinterId, job.PrinterId, StringComparison.Ordinal))?.Name);

      string? state = journal.GetState(job.JobId);
      if(state is "SPOOLED" or "SPOOLER_COMPLETED" or "FAILED" or "OUTCOME_UNKNOWN")
      {
         return;
      }

      bool submitting = false;
      try
      {
         Record(job, "RECEIVED");
         await FlushAsync(token).ConfigureAwait(false);
         byte[] content = await api.DownloadAsync(job, token).ConfigureAwait(false);
         if(!printers.Discover().Any(p => string.Equals(p.PrinterId, job.PrinterId, StringComparison.Ordinal) && p.Formats.Contains(job.Format, StringComparer.Ordinal)))
         {
            throw new InvalidOperationException("Assigned printer or document format is unavailable.");
         }

         Record(job, "SUBMITTING");
         await FlushAsync(token).ConfigureAwait(false);
         token.ThrowIfCancellationRequested();
         submitting = true;
         string spoolId = await printers.PrintAsync(job, content, token).ConfigureAwait(false);
         Record(job, "SPOOLED", spoolerJobId: spoolId);
      }
      catch(Exception exception) when(exception is not OperationCanceledException || submitting)
      {
         Record(job, submitting ? "OUTCOME_UNKNOWN" : "FAILED", exception.Message);
      }
      await FlushAsync(token).ConfigureAwait(false);
   }

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
      foreach((string jobId, PrintEvent printEvent) in journal.Pending())
      {
         await api.EventAsync(jobId, printEvent, token).ConfigureAwait(false);
         journal.Acknowledge(printEvent.EventId);
      }
   }
}



