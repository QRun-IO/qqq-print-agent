/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.Service;

/// <summary>The named requests: each sends the operation and fields the service expects, and decides success from the reply the way the window used to decide it by hand.</summary>
public sealed class ServiceClientOperationsTests
{
   private readonly List<PipeRequest> _sent = [];
   private string _reply = "ok";



   /// <summary>Builds a client whose service records every request and answers with the current scripted reply.</summary>
   private ServiceClient Client(string serverName = "QQQ") => new(request =>
   {
      _sent.Add(request);
      return Task.FromResult(_reply);
   }, new RecordingLog(), serverName);



   /// <summary>Writes a value the way the service does, so a test reply has the real wire shape.</summary>
   private static string Json<T>(T value) => JsonSerializer.Serialize(value, PrintApiClient.JsonOptions);



   /// <summary>The read operations send their operation name alone and return the typed document.</summary>
   [Fact]
   public async Task ReadOperations_SendTheirOperationAndReturnTypedDocuments()
   {
      ServiceClient client = Client();
      _reply = Json(new AgentSnapshot(true, true, "GREEN", "Ready", "https://print.example.com", "device-1", null, [], [], []));
      ServiceResult<AgentSnapshot> snapshot = await client.SnapshotAsync();
      _reply = Json(new AgentTransportSnapshot("WSS", "CONNECTED", 1, 2, 3, 4, DateTimeOffset.UnixEpoch));
      ServiceResult<AgentTransportSnapshot> transport = await client.TransportAsync();
      _reply = Json(new LogSettings(RetentionDays: 14, SegmentsToday: 2));
      ServiceResult<LogSettings> logSettings = await client.GetLogSettingsAsync();
      _reply = Json(new ServerTrust(true, "https://dev.example.com"));
      ServiceResult<ServerTrust> trust = await client.GetServerTrustAsync();

      Assert.Equal("GREEN", snapshot.Payload?.Health);
      Assert.Equal("WSS", transport.Payload?.Protocol);
      Assert.Equal(14, logSettings.Payload?.RetentionDays);
      Assert.Equal("https://dev.example.com", trust.Payload?.Origin);
      Assert.Equal("snapshot,transport,get-log-settings,get-server-trust", string.Join(',', _sent.Select(request => request.Operation)));
   }



   /// <summary>Sharing a printer names the printer and the new state, and returns the snapshot the service holds afterwards.</summary>
   [Fact]
   public async Task SetExposure_NamesThePrinterAndReturnsTheNewSnapshot()
   {
      ServiceClient client = Client();
      _reply = Json(new AgentSnapshot(true, true, "ORANGE", "Ready", null, null, null, [], [], []));

      ServiceResult<AgentSnapshot> result = await client.SetExposureAsync("printer-7", exposed: true);

      PipeRequest request = Assert.Single(_sent);
      Assert.Equal("set-exposure", request.Operation);
      Assert.Equal("printer-7", request.PrinterId);
      Assert.True(request.Exposed);
      Assert.Equal("ORANGE", result.Payload?.Health);
   }



   /// <summary>A job query carries the query unchanged and returns the page.</summary>
   [Fact]
   public async Task QueryJobs_CarriesTheQueryAndReturnsThePage()
   {
      ServiceClient client = Client();
      JobQuery query = new(Search: "inv", Status: "FAILED", Offset: 50, SortBy: "title", Descending: false);
      _reply = Json(new JobPage([], 0, 0, 50));

      ServiceResult<JobPage> result = await client.QueryJobsAsync(query);

      Assert.Same(query, Assert.Single(_sent).JobQuery);
      Assert.Equal("query-jobs", _sent[0].Operation);
      Assert.Empty(result.Payload!.Jobs);
   }



   /// <summary>Job details for the requested job are returned with their events.</summary>
   [Fact]
   public async Task JobDetails_ForTheRequestedJob_AreReturned()
   {
      ServiceClient client = Client();
      JobSnapshot job = new("job-9", "SPOOLED", "2026-11-10T10:00:00Z", null, "42");
      _reply = Json(new JobDetailsSnapshot(job, [new JobEventSnapshot("event-1", "SPOOLED", "2026-11-10T10:00:00Z", null, "42", true)], HistoryPruned: false));

      ServiceResult<JobDetailsSnapshot> result = await client.JobDetailsAsync("job-9");

      Assert.Equal("job-details", Assert.Single(_sent).Operation);
      Assert.Equal("job-9", _sent[0].JobId);
      Assert.Equal("event-1", Assert.Single(result.Payload!.Events).EventId);
   }



   /// <summary>Details that name another job, or lack the job or its events, are unreadable rather than shown under the wrong row; a service error stays a rejection.</summary>
   [Theory]
   [InlineData("""{"job":{"jobId":"job-other","status":"SPOOLED","updatedAt":"2026-11-10T10:00:00Z"},"events":[],"historyPruned":false}""", ServiceOutcome.Unavailable)]
   [InlineData("""{"events":[],"historyPruned":false}""", ServiceOutcome.Unavailable)]
   [InlineData("""{"job":{"jobId":"job-9","status":"SPOOLED","updatedAt":"2026-11-10T10:00:00Z"},"historyPruned":false}""", ServiceOutcome.Unavailable)]
   [InlineData("Error: Job job-9 was not found.", ServiceOutcome.Rejected)]
   public async Task JobDetails_ThatDoNotMatchTheRequest_AreNotReturned(string reply, ServiceOutcome expected)
   {
      ServiceClient client = Client();
      _reply = reply;

      ServiceResult<JobDetailsSnapshot> result = await client.JobDetailsAsync("job-9");

      Assert.Equal(expected, result.Outcome);
      Assert.Null(result.Payload);
      Assert.Equal(expected == ServiceOutcome.Unavailable ? ServiceFailure.UnreadableReply : ServiceFailure.None, result.Failure);
   }



   /// <summary>Saving the certificate exception and asking for a reconnect send their fields and return the service's sentence.</summary>
   [Fact]
   public async Task TextOperations_SendTheirFieldsAndReturnTheSentence()
   {
      ServiceClient client = Client();
      ServerTrust trust = new(true, "https://dev.example.com");
      _reply = "Saved.";
      ServiceResult<string> saved = await client.SetServerTrustAsync(trust);
      _reply = "Reconnect requested.";
      ServiceResult<string> reconnect = await client.ReconnectAsync();

      Assert.Equal("Saved.", saved.Payload);
      Assert.Equal("Reconnect requested.", reconnect.Payload);
      Assert.Equal("set-server-trust", _sent[0].Operation);
      Assert.Same(trust, _sent[0].ServerTrust);
      Assert.Equal("reconnect", _sent[1].Operation);
   }



   /// <summary>Enrollment succeeds only on the service's "Enrolled device" sentence; any other reply is a rejection whose message is the reply without the error marker when it has one, while the whole reply, which is what the operator is shown, stays available.</summary>
   [Theory]
   [InlineData("Enrolled device device-1", ServiceOutcome.Success, "")]
   [InlineData("Error: The enrollment code was not accepted.", ServiceOutcome.Rejected, "The enrollment code was not accepted.")]
   [InlineData("The server refused this device.", ServiceOutcome.Rejected, "The server refused this device.")]
   public async Task Enroll_SucceedsOnlyOnTheEnrolledSentence(string reply, ServiceOutcome expected, string message)
   {
      ServiceClient client = Client();
      _reply = reply;

      ServiceResult<string> result = await client.EnrollAsync("https://print.example.com", "code-123");

      PipeRequest request = Assert.Single(_sent);
      Assert.Equal("enroll", request.Operation);
      Assert.Equal("https://print.example.com", request.ServerUrl);
      Assert.Equal("code-123", request.EnrollmentCode);
      Assert.Equal(expected, result.Outcome);
      Assert.Equal(message, result.Message);
      Assert.Equal(reply, result.Reply);
   }



   /// <summary>
   /// Sign-in hands over only the credential and the pairing choice. The service confirms with "Signed in to" and this
   /// edition's server name, and the rest of its sentence becomes a value: printing is enabled, this computer still has to
   /// be enrolled, or some other condition. A reply for another server's name, or an error, is a rejection whose message
   /// has no error marker. The sentences are the ones the service really sends.
   /// </summary>
   [Theory]
   [InlineData("QQQ", "Signed in to QQQ. Printing is enabled.", SignInReadiness.PrintingEnabled, null)]
   [InlineData("Forge", "Signed in to Forge. Enroll this computer to enable printing.", SignInReadiness.EnrollmentNeeded, null)]
   [InlineData("QQQ", "Signed in to QQQ. QQQ authorization is required. Sign in again; pairing and history are preserved.", SignInReadiness.NotReady, null)]
   [InlineData("Forge", "Signed in to QQQ. Printing is enabled.", null, "Signed in to QQQ. Printing is enabled.")]
   [InlineData("QQQ", "Error: The session belongs to another user.", null, "The session belongs to another user.")]
   public async Task SignIn_IsConfirmedByTheServiceSentenceAndSaysWhatIsStillMissing(string serverName, string reply, SignInReadiness? readiness, string? rejection)
   {
      ServiceClient client = Client(serverName);
      QqqSessionCredential credential = new(new Uri("https://print.example.com"), "session-token");
      _reply = reply;

      ServiceResult<SignInConfirmation> result = await client.SignInAsync(credential, claimLegacyDevice: true, startNewPairing: false);

      PipeRequest request = Assert.Single(_sent);
      Assert.Equal("sign-in", request.Operation);
      Assert.Same(credential, request.SessionCredential);
      Assert.True(request.ClaimLegacyDevice);
      Assert.False(request.StartNewPairing);
      Assert.Equal(reply, result.Reply);
      Assert.Equal(readiness is null ? ServiceOutcome.Rejected : ServiceOutcome.Success, result.Outcome);
      Assert.Equal(readiness, result.Payload?.Readiness);
      Assert.Equal(readiness is null ? null : reply, result.Payload?.Message);
      Assert.Equal(rejection ?? string.Empty, result.Message);
   }



   /// <summary>A sign-out reply that is a document keeps its message and the provider sign-out; a plain sentence becomes a result whose message is the sentence and that has no provider sign-out; an error is a rejection.</summary>
   [Fact]
   public async Task SignOut_ReadsADocumentOrKeepsTheSentence()
   {
      ServiceClient client = Client();
      QqqProviderLogout logout = new(new Uri("https://id.example.com/logout?id_token_hint=secret"), new Uri("io.qrun.qqq-print-agent:/oauth/callback"), "state-1");
      _reply = Json(new PipeSignOutResult("Signed out.", logout));
      ServiceResult<PipeSignOutResult> document = await client.SignOutAsync("io.qrun.qqq-print-agent:/oauth/callback");
      _reply = "Signed out of QQQ.";
      ServiceResult<PipeSignOutResult> sentence = await client.SignOutAsync("io.qrun.qqq-print-agent:/oauth/callback");
      _reply = "Error: The sign-out callback is not this application's registered callback.";
      ServiceResult<PipeSignOutResult> refused = await client.SignOutAsync("io.qrun.qqq-print-agent:/oauth/callback");

      Assert.Equal("sign-out", _sent[0].Operation);
      Assert.Equal("io.qrun.qqq-print-agent:/oauth/callback", _sent[0].PostLogoutRedirectUri);
      Assert.Equal("Signed out.", document.Payload?.Message);
      Assert.Equal("state-1", document.Payload?.ProviderLogout?.State);
      Assert.Equal("Signed out of QQQ.", sentence.Payload?.Message);
      Assert.Null(sentence.Payload?.ProviderLogout);
      Assert.Equal(ServiceOutcome.Rejected, refused.Outcome);
      Assert.Equal("The sign-out callback is not this application's registered callback.", refused.Message);
   }



   /// <summary>
   /// A sign-out reply that opens like a JSON object is the document that carries the identity provider's token hint, so it
   /// is never used as text. Without a message, or when it cannot be read, it is an unreadable reply: nothing of it reaches
   /// the result or the log.
   /// </summary>
   [Theory]
   [InlineData("""{"providerLogout":{"endSessionUri":"https://id.example.com/logout?id_token_hint=secret-hint","redirectUri":"io.qrun.qqq-print-agent:/oauth/callback","state":"state-1"}}""")]
   [InlineData("""  {"message":"Signed out.","providerLogout":"https://id.example.com/logout?id_token_hint=secret-hint" """)]
   [InlineData("{}")]
   public async Task SignOutDocumentWithoutAReadableMessage_IsNeverUsedAsText(string reply)
   {
      RecordingLog log = new();
      ServiceClient client = new(_ => Task.FromResult(reply), log, "QQQ");

      ServiceResult<PipeSignOutResult> result = await client.SignOutAsync("io.qrun.qqq-print-agent:/oauth/callback");

      Assert.Equal(ServiceOutcome.Unavailable, result.Outcome);
      Assert.Equal(ServiceFailure.UnreadableReply, result.Failure);
      Assert.True(result.Unconfirmed);
      Assert.Null(result.Payload);
      Assert.Null(result.Reply);
      Assert.Equal("The service's reply could not be read.", result.Message);
      Assert.DoesNotContain(log.Entries, entry => entry.Contains("secret-hint", StringComparison.Ordinal));
   }
}
