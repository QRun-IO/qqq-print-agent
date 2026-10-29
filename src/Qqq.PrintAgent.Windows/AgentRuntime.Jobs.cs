using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

public sealed partial class AgentRuntime
{
   private async Task<string> HandleJobCommandAsync(PipeRequest request, CancellationToken token)
   {
      using AgentStore store = new(directory);
      if(string.Equals(request.Operation, "job-details", StringComparison.Ordinal))
      {
         JobDetailsSnapshot? details = store.GetJobDetails(RequiredJob(request));
         lock(_stateLock)
         {
            JobSnapshot? queued = _queuedJobs.FirstOrDefault(job => string.Equals(job.JobId, request.JobId, StringComparison.Ordinal));
            if(details is null && queued is not null)
            { details = new JobDetailsSnapshot(queued, [], false); }
         }
         return JsonSerializer.Serialize(details ?? throw new InvalidOperationException("Job not found in this connection."), PrintApiClient.JsonOptions);
      }
      if(string.Equals(request.Operation, "acknowledge-job", StringComparison.Ordinal))
      {
         store.AcknowledgeFailure(RequiredJob(request));
         WriteLog("INFO", "Operator acknowledged a job that needed attention.");
         return "Job acknowledged. This does not change its delivery outcome or print it again.";
      }
      if(string.Equals(request.Operation, "cancel-job", StringComparison.Ordinal))
      {
         JobSnapshot? job = store.GetJobDetails(RequiredJob(request))?.Job;
         if(job is { Status: "SPOOLED", SpoolerJobId: not null, PrinterId: not null })
         {
            PrinterInfo printer = _printers.Discover().Single(p => string.Equals(p.PrinterId, job.PrinterId, StringComparison.Ordinal));
            if(_printers is not IPrinterJobControl control)
            {
               throw new InvalidOperationException("Restart with the current installed service to use bounded spooler cancellation.");
            }
            await control.CancelAsync(printer.Name, job.JobId, job.SpoolerJobId, token).ConfigureAwait(false);
            WriteLog("INFO", "Operator requested cancellation of a Windows spooler job.");
            return "Cancellation requested from Windows. Pages already sent to the printer may still print; inspect the output.";
         }
      }
      AgentConfiguration configuration = store.LoadConfiguration() ?? throw new InvalidOperationException("Connect this device in Settings first.");
      if(string.Equals(request.Operation, "test-print", StringComparison.Ordinal))
      {
         PrinterInfo printer = _printers.Discover().SingleOrDefault(p => string.Equals(p.PrinterId, request.PrinterId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Select an available printer.");
         if(!store.GetExposedPrinterIds().Contains(printer.PrinterId) || !printer.Formats.Contains(request.Format ?? "", StringComparer.Ordinal))
         {
            throw new InvalidOperationException("Share the printer and select a supported format first. QQQ must also approve the printer.");
         }
      }
      using PrintApiClient api = CreateApi(configuration);
      string queuedJob = await api.OperatorActionAsync(request.Operation, request.JobId, request.PrinterId, request.Format, request.ConfirmDuplicateRisk, token).ConfigureAwait(false);
      if(string.Equals(request.Operation, "cancel-job", StringComparison.Ordinal))
      {
         lock(_stateLock)
         {
            _queueGeneration++;
            _queuedJobs = _queuedJobs.Where(job => !string.Equals(job.JobId, request.JobId, StringComparison.Ordinal)).ToArray();
         }
      }
      WriteLog("INFO", "QQQ accepted operator action " + request.Operation + ".");
      WakePoll();
      return string.IsNullOrEmpty(queuedJob) ? "QQQ accepted the cancellation before dispatch." : "Queued in QQQ. The new job will appear here when this device receives it.";
   }

   private static string RequiredJob(PipeRequest request) => !string.IsNullOrWhiteSpace(request.JobId) ? request.JobId : throw new InvalidOperationException("Select a job first.");
}
