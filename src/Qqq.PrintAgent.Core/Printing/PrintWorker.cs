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
      await FlushAsync(token).ConfigureAwait(false);
      await api.HeartbeatAsync(token).ConfigureAwait(false);
      await api.InventoryAsync(printers.Discover(), token).ConfigureAwait(false);
      PrintJob? job = await api.ClaimAsync(token).ConfigureAwait(false);
      if(job is null)
      {
         return;
      }

      string? state = journal.GetState(job.JobId);
      if(state is "SUBMITTING")
      {
         Record(job, "OUTCOME_UNKNOWN", "Agent restarted during submission; inspect the printer before reprinting.");
         await FlushAsync(token).ConfigureAwait(false);
         return;
      }
      if(state is "SPOOLED" or "FAILED" or "OUTCOME_UNKNOWN")
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
      journal.Record(job.JobId, new PrintEvent(Guid.NewGuid().ToString("N"), state, message, spoolerJobId));
      log?.Invoke($"{job.JobId}: {state}");
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



