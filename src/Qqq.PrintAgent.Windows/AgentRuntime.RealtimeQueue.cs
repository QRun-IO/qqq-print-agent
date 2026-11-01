/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Cloud queue preview part of the runtime. It shows jobs the server holds but this device has not received yet, and discards a result
/// if the connection or queue changed while it was being fetched, so the preview never shows another pairing's jobs.
/// </summary>
public sealed partial class AgentRuntime
{
   /// <summary>Refresh the bounded cloud preview only after a server or operator signal, including while dispatch is paused.</summary>
   private async Task RefreshQueuedOnSignalAsync(CancellationToken token)
   {
      while(!token.IsCancellationRequested)
      {
         await _queueRefreshRequested.WaitAsync(token).ConfigureAwait(false);
         if(!_realtimeReady)
         { continue; }
         try
         {
            using var store = new AgentStore(directory);
            AgentConfiguration? configuration = store.LoadConfiguration();
            if(configuration is null)
            { continue; }
            long connection;
            long generation;
            lock(_stateLock)
            { connection = _connectionGeneration; generation = _queueGeneration; }
            using PrintApiClient api = CreateApi(configuration);
            IReadOnlyList<PrintJob> jobs = await api.QueuedJobsAsync(token).ConfigureAwait(false);
            lock(_stateLock)
            {
               if(connection != _connectionGeneration || generation != _queueGeneration)
               { continue; }
               _queuedJobs = jobs.Take(50).Select(job => new JobSnapshot(job.JobId, "QUEUED", job.CreatedAt,
                  "Waiting on the server; not yet received by this device.", null, job.PrinterId,
                  _queuedJobs.FirstOrDefault(previous => string.Equals(previous.PrinterId, job.PrinterId, StringComparison.Ordinal))?.PrinterName,
                  job.Title, job.Format, CreatedAt: JobSorting.NormalizeCreatedAt(job.CreatedAt))).ToArray();
            }
         }
         catch(Exception exception) when(exception is not OperationCanceledException || !token.IsCancellationRequested)
         {
            WriteLog("ERROR", "Cloud queue preview could not be refreshed: " + exception.Message);
         }
      }
   }
}
