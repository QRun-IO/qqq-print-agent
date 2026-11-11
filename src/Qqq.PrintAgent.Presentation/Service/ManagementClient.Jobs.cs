/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Service;

/// <summary>The operations on one server's jobs. Each takes the server's full identifier, as the snapshot gives it; text that is not one, none at all, and a job or a query that is missing are not sent, and the result is the refusal the service would give, <see cref="ManagementErrorCode.InvalidRequest"/>.</summary>
public sealed partial class ManagementClient
{
   /// <summary>
   /// Reads one filtered, sorted page of a server's jobs. The Jobs page repeats it on a timer, so over the ceiling it
   /// is skipped (<see cref="ServiceOutcome.Skipped"/>) and asked again on the next tick.
   /// </summary>
   public Task<ServiceResult<JobPage>> QueryJobsAsync(string serverId, JobQuery query, CancellationToken cancellationToken = default)
      => AskAsync<JobPage>(ManagementOperations.JobsQuery, operation => WithArguments(operation, serverId, new JobsQueryArgs(query)), cancellationToken, repeated: true);



   /// <summary>Reads one job and its recorded events. A reply that holds another job's details is unreadable, so details can never be shown under the wrong row; a job's identifier is compared exactly, letter case included. An operator asks for it once, so over the ceiling it waits its turn.</summary>
   public Task<ServiceResult<JobDetailsSnapshot>> JobDetailsAsync(string serverId, string jobId, CancellationToken cancellationToken = default)
      => AskAsync<JobDetailsSnapshot>(ManagementOperations.JobsDetails, operation => WithArguments(operation, serverId, new JobsDetailsArgs(jobId)), cancellationToken,
         details => string.Equals(details.Job.JobId, jobId, StringComparison.Ordinal));



   /// <summary>
   /// Asks a server to queue a job again. A retry that could print twice is refused with
   /// <see cref="ManagementErrorCode.DuplicateRiskUnconfirmed"/> unless the operator has confirmed that risk, which is
   /// what <paramref name="confirmDuplicateRisk"/> states. The result names the new job when the server gave it an
   /// identifier. Over the ceiling the request waits its turn.
   /// </summary>
   public Task<ServiceResult<JobQueuedResult>> RetryJobAsync(string serverId, string jobId, bool confirmDuplicateRisk, CancellationToken cancellationToken = default)
      => AskAsync<JobQueuedResult>(ManagementOperations.JobsRetry, operation => WithArguments(operation, serverId, new JobsRetryArgs(jobId, confirmDuplicateRisk)), cancellationToken);



   /// <summary>Cancels a job: in the Windows spooler when it is there, otherwise on the server. The result says where. When Windows refuses, the refusal is <see cref="ManagementErrorCode.CancelRefused"/> and its details say why. Over the ceiling it waits its turn.</summary>
   public Task<ServiceResult<JobCancelResult>> CancelJobAsync(string serverId, string jobId, CancellationToken cancellationToken = default)
      => AskAsync<JobCancelResult>(ManagementOperations.JobsCancel, operation => WithArguments(operation, serverId, new JobsCancelArgs(jobId)), cancellationToken);



   /// <summary>Marks a failed job as seen by the operator. Over the ceiling it waits its turn.</summary>
   public Task<ServiceResult<NoResult>> AcknowledgeJobAsync(string serverId, string jobId, CancellationToken cancellationToken = default)
      => AskAsync<NoResult>(ManagementOperations.JobsAcknowledge, operation => WithArguments(operation, serverId, new JobsAcknowledgeArgs(jobId)), cancellationToken);
}
