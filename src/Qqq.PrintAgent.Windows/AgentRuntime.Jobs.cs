using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

public sealed partial class AgentRuntime
{
   private async Task<string> HandleJobCommandAsync(PipeRequest request, CancellationToken token)
   {
      using AgentStore store = new(directory);
      if(string.Equals(request.Operation, "query-jobs", StringComparison.Ordinal))
      {
         JobSnapshot[] queued;
         lock(_stateLock)
         {
            queued = _queuedJobs.Take(50).ToArray();
         }
         return JsonSerializer.Serialize(QueryJobsWithQueue(store, request.JobQuery ?? new JobQuery(), queued), PrintApiClient.JsonOptions);
      }
      if(string.Equals(request.Operation, "job-details", StringComparison.Ordinal))
      {
         return SerializeJobDetails(store, request);
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
      return await SendOperatorActionAsync(store, request, token).ConfigureAwait(false);
   }

   private async Task<string> SendOperatorActionAsync(AgentStore store, PipeRequest request, CancellationToken token)
   {
      if(!NormalTrafficAllowed)
      {
         throw new InvalidOperationException("Server actions are unavailable while disconnected by the administrator or awaiting authorization/control negotiation. Local history is preserved.");
      }
      AgentConfiguration configuration = store.LoadConfiguration() ?? throw new InvalidOperationException("Connect this device in Settings first.");
      if(string.Equals(request.Operation, "test-print", StringComparison.Ordinal))
      {
         PrinterInfo printer = _printers.Discover().SingleOrDefault(p => string.Equals(p.PrinterId, request.PrinterId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Select an available printer.");
         if(!store.GetExposedPrinterIds().Contains(printer.PrinterId) || !printer.Formats.Contains(request.Format ?? "", StringComparer.Ordinal))
         {
            throw new InvalidOperationException("Share the printer and select a supported format first. The server must also approve the printer.");
         }
      }
      using PrintApiClient api = CreateApi(configuration);
      string? testProfile = string.Equals(request.Operation, "test-print", StringComparison.Ordinal) ? request.TestProfile : null;
      string queuedJob = await api.OperatorActionAsync(request.Operation, request.JobId, request.PrinterId, request.Format, request.ConfirmDuplicateRisk, testProfile, token).ConfigureAwait(false);
      if(string.Equals(request.Operation, "cancel-job", StringComparison.Ordinal))
      {
         lock(_stateLock)
         {
            _queueGeneration++;
            _queuedJobs = _queuedJobs.Where(job => !string.Equals(job.JobId, request.JobId, StringComparison.Ordinal)).ToArray();
         }
      }
      WriteLog("INFO", "Server accepted operator action " + request.Operation + ".");
      WakePoll();
      return string.IsNullOrEmpty(queuedJob) ? "The server accepted the cancellation before dispatch." : "Queued on the server. The new job will appear here when this device receives it.";
   }

   private string SerializeJobDetails(AgentStore store, PipeRequest request)
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

   private static string RequiredJob(PipeRequest request) => !string.IsNullOrWhiteSpace(request.JobId) ? request.JobId : throw new InvalidOperationException("Select a job first.");

   internal static JobPage QueryJobsWithQueue(AgentStore store, JobQuery query, IReadOnlyList<JobSnapshot> queued)
   {
      _ = AgentStore.ValidateJobQuery(query);
      JobSnapshot[] cloud = queued.Take(50).DistinctBy(job => job.JobId, StringComparer.Ordinal)
         .Where(job => MatchesQuery(job, query) && !store.HasJob(job.JobId)).ToArray();
      int localStart = Math.Max(0, query.Offset - cloud.Length);
      int localCount = query.Offset - localStart + query.PageSize;
      JobPage first = store.QueryJobs(query with { Offset = localStart, PageSize = Math.Min(localCount, 100) });
      List<JobSnapshot> candidates = [.. first.Jobs, .. cloud];
      if(localCount > 100)
      {
         JobPage second = store.QueryJobs(query with { Offset = localStart + 100, PageSize = localCount - 100 });
         candidates.AddRange(second.Jobs);
      }
      candidates.Sort((left, right) => JobSorting.Compare(left, right, query));
      JobSnapshot[] page = candidates.Skip(query.Offset - localStart).Take(query.PageSize).ToArray();
      return new JobPage(page, first.TotalCount + cloud.Length, query.Offset, query.PageSize, queued.Count >= 50);
   }

   private static bool MatchesQuery(JobSnapshot job, JobQuery query)
   {
      if(!string.IsNullOrWhiteSpace(query.Status) && !string.Equals(job.Status, query.Status, StringComparison.Ordinal))
      {
         return false;
      }
      if(!string.IsNullOrWhiteSpace(query.PrinterId) && !string.Equals(job.PrinterId, query.PrinterId, StringComparison.Ordinal))
      {
         return false;
      }
      if(query.NeedsAttentionOnly && (job.Status is not ("FAILED" or "OUTCOME_UNKNOWN") || job.FailureAcknowledged))
      {
         return false;
      }
      string? search = query.Search?.Trim();
      return string.IsNullOrEmpty(search) || JobSorting.MatchesPrefix(job.JobId, search)
         || JobSorting.MatchesPrefix(job.Title, search)
         || JobSorting.MatchesPrefix(job.PrinterName, search);
   }
}
