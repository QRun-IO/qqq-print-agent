/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Service;

/// <summary>
/// The named requests the companion makes. Each builds its pipe request and states what a successful reply is, so views and
/// view models never spell an operation name or parse a reply themselves.
/// </summary>
public sealed partial class ServiceClient
{
   /// <summary>Reads the service's health, printers and connection state.</summary>
   public Task<ServiceResult<AgentSnapshot>> SnapshotAsync(CancellationToken cancellationToken = default)
      => SendAsync<AgentSnapshot>(new PipeRequest("snapshot"), cancellationToken);



   /// <summary>Reads the WebSocket state and traffic counters for the footer; a local observation, not a request to the server.</summary>
   public Task<ServiceResult<AgentTransportSnapshot>> TransportAsync(CancellationToken cancellationToken = default)
      => SendAsync<AgentTransportSnapshot>(new PipeRequest("transport"), cancellationToken);



   /// <summary>Shares or unshares one printer and returns the snapshot the service holds afterwards, so the list shows what was really saved.</summary>
   public Task<ServiceResult<AgentSnapshot>> SetExposureAsync(string printerId, bool exposed, CancellationToken cancellationToken = default)
      => SendAsync<AgentSnapshot>(new PipeRequest("set-exposure", PrinterId: printerId, Exposed: exposed), cancellationToken);



   /// <summary>Asks for one filtered, sorted page of job history.</summary>
   public Task<ServiceResult<JobPage>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken = default)
      => SendAsync<JobPage>(new PipeRequest("query-jobs", JobQuery: query), cancellationToken);



   /// <summary>
   /// Reads one job's details and event history. A reply without a job, without events, or for a different job is treated as
   /// unreadable, so details can never be shown under the wrong row.
   /// </summary>
   public Task<ServiceResult<JobDetailsSnapshot>> JobDetailsAsync(string jobId, CancellationToken cancellationToken = default)
      => ExchangeAsync(new PipeRequest("job-details", JobId: jobId), reply => Marked(reply, text => ReadJobDetails(text, jobId)), cancellationToken);



   /// <summary>Reads the shared diagnostic log limits and the current file's size.</summary>
   public Task<ServiceResult<LogSettings>> GetLogSettingsAsync(CancellationToken cancellationToken = default)
      => SendAsync<LogSettings>(new PipeRequest("get-log-settings"), cancellationToken);



   /// <summary>Reads the saved certificate exception and the one server it applies to.</summary>
   public Task<ServiceResult<ServerTrust>> GetServerTrustAsync(CancellationToken cancellationToken = default)
      => SendAsync<ServerTrust>(new PipeRequest("get-server-trust"), cancellationToken);



   /// <summary>Saves the certificate exception so the service's own sessions honor it.</summary>
   public Task<ServiceResult<string>> SetServerTrustAsync(ServerTrust trust, CancellationToken cancellationToken = default)
      => SendAsync(new PipeRequest("set-server-trust", ServerTrust: trust), cancellationToken);



   /// <summary>Asks the service to try its server connection again now.</summary>
   public Task<ServiceResult<string>> ReconnectAsync(CancellationToken cancellationToken = default)
      => SendAsync(new PipeRequest("reconnect"), cancellationToken);



   /// <summary>Enrolls this device with an enrollment code. Success is the service's "Enrolled device" sentence; anything else is a rejection.</summary>
   public Task<ServiceResult<string>> EnrollAsync(string serverUrl, string enrollmentCode, CancellationToken cancellationToken = default)
      => ExchangeAsync(new PipeRequest("enroll", serverUrl, enrollmentCode), static reply => Opening(reply, "Enrolled device ", static text => text), cancellationToken);



   /// <summary>
   /// Hands a session credential to the service with the operator's pairing choice. The service confirms with a sentence
   /// that opens "Signed in to" and this edition's server name, and then says whether printing is enabled or what is still
   /// missing; that second half is returned as <see cref="SignInReadiness"/>, so "signed in, enrollment still needed" is a
   /// value and not text to match. A reply that does not open that way is a rejection.
   /// </summary>
   public Task<ServiceResult<SignInConfirmation>> SignInAsync(QqqSessionCredential credential, bool claimLegacyDevice, bool startNewPairing, CancellationToken cancellationToken = default)
   {
      string opening = "Signed in to " + _serverName + ".";
      return ExchangeAsync(new PipeRequest("sign-in", SessionCredential: credential, ClaimLegacyDevice: claimLegacyDevice, StartNewPairing: startNewPairing),
         reply => Opening(reply, opening, text => new SignInConfirmation(text, ReadReadiness(text[opening.Length..]))), cancellationToken);
   }



   /// <summary>
   /// Ends the service's session. The reply is a sentence, or a document that also carries the identity provider's own
   /// sign-out for the browser; a sentence is returned as a result without a provider sign-out, so callers handle one shape.
   /// </summary>
   public Task<ServiceResult<PipeSignOutResult>> SignOutAsync(string postLogoutRedirectUri, CancellationToken cancellationToken = default)
      => ExchangeAsync(new PipeRequest("sign-out", PostLogoutRedirectUri: postLogoutRedirectUri), static reply => Marked(reply, ReadSignOut), cancellationToken);



   /// <summary>
   /// Reads what the service said after its sign-in opening: printing is enabled, the computer still has to be enrolled, or
   /// some other condition. The two sentences are the service's own constants, so the service cannot reword one without
   /// this reading following it.
   /// </summary>
   private static SignInReadiness ReadReadiness(string remainder) => remainder switch
   {
      SignInSentences.PrintingEnabled => SignInReadiness.PrintingEnabled,
      SignInSentences.EnrollmentNeeded => SignInReadiness.EnrollmentNeeded,
      _ => SignInReadiness.NotReady,
   };



   /// <summary>Reads job details and refuses a document that has no job, no events or a different job's identifier.</summary>
   private static JobDetailsSnapshot ReadJobDetails(string reply, string jobId)
   {
      JobDetailsSnapshot details = ReadJson<JobDetailsSnapshot>(reply);
      return details.Job is not null && details.Events is not null && string.Equals(details.Job.JobId, jobId, StringComparison.Ordinal)
         ? details : throw new JsonException("Job details do not match the requested job.");
   }



   /// <summary>
   /// Reads the sign-out reply. Text that does not open like a JSON object is the service's sentence and becomes the message.
   /// Anything that does open like one is read as the sign-out document and must carry a message: the document is the reply
   /// that holds the identity provider's token hint, so it is never used as text, and a document that cannot be read, has
   /// no message or has an incomplete provider part is unreadable rather than shown or logged.
   /// </summary>
   private static PipeSignOutResult ReadSignOut(string reply)
   {
      if(!reply.TrimStart().StartsWith('{'))
      {
         return new PipeSignOutResult(reply, null);
      }
      return Deserialize<PipeSignOutResult>(reply) is { Message: not null } document
         ? document : throw new JsonException("The sign-out document has no message.");
   }
}
