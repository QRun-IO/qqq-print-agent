/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Service;

/// <summary>
/// How the version 2 client turns a parsed reply and a pipe failure into a result: a refusal carries the contract's
/// code and typed details, a reply that cannot be used says which side is older or that it was unreadable, a pipe
/// failure says its cause and whether the request had left, nothing is thrown for what a pipe can do, and every
/// swallowed failure is written to the log once as a warning.
/// </summary>
public sealed class ManagementClientTests
{
   /// <summary>The server the requests of these tests name.</summary>
   private const string Server = "3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f";



   /// <summary>Another server.</summary>
   private const string OtherServer = "3f2b8c1e-0000-4f6b-9a7c-0e1d2c3b4a60";



   /// <summary>A success is the payload and nothing else: no code, no message, no failure, nothing to check, no reply text kept and nothing logged.</summary>
   [Fact]
   public async Task Success_HasThePayloadAndNothingElse()
   {
      RecordingLog log = new();
      FakePipe pipe = new FakePipe().Answer(Replies.Success(new ReconnectResult(ReconnectResult.HeldByAdministrator)));
      ManagementClient client = new(pipe.SendAsync, log);

      ServiceResult<ReconnectResult> result = await client.ReconnectAsync(Server);

      Assert.Equal(ServiceOutcome.Success, result.Outcome);
      Assert.True(result.Succeeded);
      Assert.Equal(ReconnectResult.HeldByAdministrator, result.Payload.Outcome);
      Assert.Null(result.Code);
      Assert.Equal(string.Empty, result.Message);
      Assert.Null(result.Reply);
      Assert.Equal(ServiceFailure.None, result.Failure);
      Assert.False(result.Unconfirmed);
      Assert.False(result.TryReadDetails(out CancelRefusedDetails? _));
      Assert.Empty(log.Entries);
   }



   /// <summary>A refusal is a rejection carrying the contract's code and the service's message. The service answered, so nothing is unconfirmed, and a refusal is not a failure to log. Each code that has details gives them as the record the contract names for it.</summary>
   [Fact]
   public async Task Failure_CarriesCodeMessageAndTypedDetails()
   {
      RecordingLog log = new();
      FakePipe pipe = new FakePipe().Answer(Replies.Golden("error.cancel_refused.json")).Answer(Replies.Golden("error.identity_changed.json"))
         .Answer(Replies.Golden("error.enrollment_unconfirmed.json")).Answer(Replies.Golden("error.certificate_untrusted.json")).Answer(Replies.Golden("error.server_incompatible.json"));
      ManagementClient client = new(pipe.SendAsync, log);

      ServiceResult<JobCancelResult> cancel = await client.CancelJobAsync(Server, "job-204");
      ServiceResult<SignInResult> signIn = await client.SignInAsync(Server, SampleValues.Golden.Credential);
      ServiceResult<EnrollResult> enroll = await client.EnrollAsync(Server, "code");
      ServiceResult<ServerCheckResult> untrusted = await client.CheckServerAsync("https://qqq.example.test", acceptCertificateException: false);
      ServiceResult<ServerCheckResult> incompatible = await client.CheckServerAsync("https://qqq.example.test", acceptCertificateException: false);

      Assert.Equal((ServiceOutcome.Rejected, ManagementErrorCode.CancelRefused, Replies.Message, ServiceFailure.None), (cancel.Outcome, cancel.Code, cancel.Message, cancel.Failure));
      Assert.False(cancel.Succeeded || cancel.Unconfirmed);
      Assert.Null(cancel.Payload);
      Assert.Null(cancel.Reply);
      Assert.True(cancel.TryReadDetails(out CancelRefusedDetails? whyNot));
      Assert.Equal(CancelRefusedDetails.OwnershipMismatch, whyNot.Reason);
      Assert.Equal(ManagementErrorCode.IdentityChanged, signIn.Code);
      Assert.True(signIn.TryReadDetails(out IdentityChangedDetails? approved));
      Assert.Equal(("principal-4", "tenant-2"), (approved.ApprovedPrincipalId, approved.ApprovedTenantId));
      Assert.Equal(ManagementErrorCode.EnrollmentUnconfirmed, enroll.Code);
      Assert.True(enroll.TryReadDetails(out EnrollmentUnconfirmedDetails? left));
      Assert.Equal((EnrollmentUnconfirmedDetails.DeviceBound, true, "device-17"), (left.Stage, left.PrintingBlocked, left.DeviceId));
      Assert.Equal(ManagementErrorCode.CertificateUntrusted, untrusted.Code);
      Assert.True(untrusted.TryReadDetails(out ServerCheckResult? refused));
      Assert.Equal("https://sign-in.example.test", refused.ProviderOrigin);
      Assert.NotNull(refused.Certificate);
      Assert.Equal(ManagementErrorCode.ServerIncompatible, incompatible.Code);
      Assert.True(incompatible.TryReadDetails(out ServerCheckResult? stages));
      Assert.Equal(CompatibilityStageResult.Order, stages.Stages.Select(stage => stage.Stage), StringComparer.Ordinal);
      Assert.Empty(log.Entries);
   }



   /// <summary>
   /// Details are read only as the record the contract names for the code. The details of one code are not read as
   /// another code's record, even one whose members are all optional and which would therefore accept any object; a
   /// code without details has none whatever the reply held; details that do not fit their record are not read; and
   /// a result that is not a refusal has none.
   /// </summary>
   [Fact]
   public async Task Details_AreReadOnlyAsTheRecordTheirCodeNames()
   {
      FakePipe pipe = new FakePipe().Answer(Replies.Golden("error.cancel_refused.json"))
         .Answer(Replies.Text("""{"v":2,"ok":false,"error":{"code":"server_not_found","message":"No such server.","details":{"reason":"documentGone"}}}"""))
         .Answer(Replies.Text("""{"v":2,"ok":false,"error":{"code":"cancel_refused","message":"Windows refused.","details":{"cause":"unknown"}}}"""))
         .Fail(new IOException("down"));
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());

      ServiceResult<JobCancelResult> refused = await client.CancelJobAsync(Server, "job-204");
      ServiceResult<JobCancelResult> withoutDetails = await client.CancelJobAsync(Server, "job-204");
      ServiceResult<JobCancelResult> unfitting = await client.CancelJobAsync(Server, "job-204");
      ServiceResult<JobCancelResult> unavailable = await client.CancelJobAsync(Server, "job-204");

      Assert.False(refused.TryReadDetails(out IdentityChangedDetails? another));
      Assert.Null(another);
      Assert.True(refused.TryReadDetails(out CancelRefusedDetails? _));
      Assert.Equal(ManagementErrorCode.ServerNotFound, withoutDetails.Code);
      Assert.False(withoutDetails.TryReadDetails(out CancelRefusedDetails? _));
      Assert.Equal((ManagementErrorCode.CancelRefused, "Windows refused."), (unfitting.Code, unfitting.Message));
      Assert.False(unfitting.TryReadDetails(out CancelRefusedDetails? _));
      Assert.Null(unavailable.Code);
      Assert.False(unavailable.TryReadDetails(out CancelRefusedDetails? _));
   }



   /// <summary>
   /// A code this version does not know is a rejection that shows the service's message and is handled as an internal
   /// error, as the contract says: the result's code is <c>internal_error</c>, and it has no details, because their
   /// type is not known either. The log says which code arrived when it is shaped like one, and does not repeat text
   /// of any other kind that arrived where a code belongs.
   /// </summary>
   [Theory]
   [InlineData("quota_exceeded", "quota_exceeded")]
   [InlineData("Bearer CANARY 123", "not shown")]
   [InlineData("", "not shown")]
   [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "not shown")]
   public async Task UnknownCode_ShowsTheMessage_AndIsHandledAsInternalError(string code, string logged)
   {
      RecordingLog log = new();
      FakePipe pipe = new FakePipe().Answer(Replies.Text("""{"v":2,"ok":false,"error":{"code":"CODE","message":"The server's quota is used up.","details":{"reason":"documentGone"}}}""".Replace("CODE", code, StringComparison.Ordinal)));
      ManagementClient client = new(pipe.SendAsync, log);

      ServiceResult<JobCancelResult> result = await client.CancelJobAsync(Server, "job-204");

      Assert.Equal(ServiceOutcome.Rejected, result.Outcome);
      Assert.Equal(ManagementErrorCode.InternalError, result.Code);
      Assert.Equal("The server's quota is used up.", result.Message);
      Assert.False(result.Unconfirmed);
      Assert.False(result.TryReadDetails(out CancelRefusedDetails? _));
      Assert.Equal($"WARN Management request jobs.cancel on {Server} was refused with a code this version does not know ({logged}); it is handled as internal_error.", Assert.Single(log.Entries));
   }



   /// <summary>
   /// A read that is refused with the same unknown code on every tick warns once: the first refusal is a warning and
   /// the same code repeating is a debug line, as a repeated failure is. Another unknown code is a warning again, and
   /// so is the same one after the operation was answered in any other way: by a refusal with a code this version
   /// knows, and by a result. The same operation on another server warns for itself. The steps here are, on one
   /// server: one code twice, another code, a known refusal, that code again, a result, and that code twice more.
   /// </summary>
   [Fact]
   public async Task UnknownCode_Repeating_IsAWarningOnce()
   {
      RecordingLog log = new();
      ManagementReplyParse quota = Replies.Text("""{"v":2,"ok":false,"error":{"code":"quota_exceeded","message":"The server's quota is used up."}}""");
      ManagementReplyParse paused = Replies.Text("""{"v":2,"ok":false,"error":{"code":"paused_by_owner","message":"Paused."}}""");
      FakePipe pipe = new FakePipe().Answer(quota).Answer(quota).Answer(quota).Answer(paused).Answer(Replies.Failure(ManagementErrorCode.Busy)).Answer(paused)
         .Answer(Replies.Success(new ReconnectResult(ReconnectResult.Requested))).Answer(paused).Answer(paused);
      ManagementClient client = new(pipe.SendAsync, log);

      _ = await client.ReconnectAsync(Server);
      _ = await client.ReconnectAsync(Server);
      ServiceResult<ReconnectResult> elsewhere = await client.ReconnectAsync(OtherServer);
      ServiceResult<ReconnectResult> last = elsewhere;
      for(int attempt = 0; attempt < 6; attempt++)
      {
         last = await client.ReconnectAsync(Server);
      }

      Assert.Equal((ManagementErrorCode.InternalError, "The server's quota is used up."), (elsewhere.Code, elsewhere.Message));
      Assert.Equal((ManagementErrorCode.InternalError, "Paused."), (last.Code, last.Message));
      Assert.Equal(
      [
         $"WARN Management request connection.reconnect on {Server} was refused with a code this version does not know (quota_exceeded); it is handled as internal_error.",
         $"DEBUG Management request connection.reconnect on {Server} was refused with a code this version does not know (quota_exceeded); it is handled as internal_error.",
         $"WARN Management request connection.reconnect on {OtherServer} was refused with a code this version does not know (quota_exceeded); it is handled as internal_error.",
         $"WARN Management request connection.reconnect on {Server} was refused with a code this version does not know (paused_by_owner); it is handled as internal_error.",
         $"WARN Management request connection.reconnect on {Server} was refused with a code this version does not know (paused_by_owner); it is handled as internal_error.",
         $"WARN Management request connection.reconnect on {Server} was refused with a code this version does not know (paused_by_owner); it is handled as internal_error.",
         $"DEBUG Management request connection.reconnect on {Server} was refused with a code this version does not know (paused_by_owner); it is handled as internal_error.",
      ], log.Entries);
   }



   /// <summary>
   /// A reply that is not JSON, or is JSON without a version, is what a version 1 service answers. That service knows
   /// none of this version's operations, so it carried nothing out: the result says the service is the older side and
   /// that there is nothing to check before repeating. The log names the problem and not the reply.
   /// </summary>
   [Theory]
   [InlineData("Error: CANARY Restart the companion.", "NotJson")]
   [InlineData("", "NotJson")]
   [InlineData("""{"enrolled":true,"health":"CANARY"}""", "VersionMissing")]
   public async Task ReplyWithoutAVersion_IsAnOlderService(string reply, string problem)
   {
      RecordingLog log = new();
      ManagementClient client = new(new FakePipe().Answer(Replies.Text(reply)).SendAsync, log);

      ServiceResult<ServerRemoveResult> result = await client.RemoveServerAsync(Server);

      Assert.Equal((ServiceOutcome.Unavailable, ServiceFailure.OlderService), (result.Outcome, result.Failure));
      Assert.False(result.Unconfirmed);
      Assert.Null(result.Code);
      Assert.Equal("The service is older than this companion and does not know its requests. Update the service.", result.Message);
      Assert.Equal($"WARN Management request servers.remove on {Server} got a reply that could not be used (OlderService: {problem}).", Assert.Single(log.Entries));
   }



   /// <summary>A reply in another version is what a newer service answers, and the only reply a service gives in another version than it was asked in is its refusal of the version. So nothing was carried out, and the result says the companion is the older side.</summary>
   [Theory]
   [InlineData("""{"v":3,"ok":false,"error":{"code":"unsupported_version","message":"CANARY"}}""")]
   [InlineData("""{"v":"2","ok":true,"result":{}}""")]
   public async Task ReplyOfAnotherVersion_IsANewerService(string reply)
   {
      RecordingLog log = new();
      ManagementClient client = new(new FakePipe().Answer(Replies.Text(reply)).SendAsync, log);

      ServiceResult<ServiceSnapshot> result = await client.SnapshotAsync();

      Assert.Equal((ServiceOutcome.Unavailable, ServiceFailure.NewerService), (result.Outcome, result.Failure));
      Assert.False(result.Unconfirmed);
      Assert.Equal("The service is newer than this companion, which cannot read its replies. Update the companion.", result.Message);
      Assert.Equal("WARN Management request snapshot on the computer got a reply that could not be used (NewerService: VersionUnsupported).", Assert.Single(log.Entries));
   }



   /// <summary>
   /// A reply of this version that cannot be read (too large, a repeated member, not an object) or that lacks what
   /// its <c>ok</c> promises is an answer that arrived and could not be used. The service may have acted, so the
   /// result is unconfirmed; its reason is fixed text; and the log names the problem and never quotes the reply.
   /// </summary>
   [Theory]
   [MemberData(nameof(UnreadableReplies))]
   public async Task UnreadableOrIncompleteReply_IsUnconfirmed_AndTheLogDoesNotQuoteIt(string reply, int trailingSpaces, string problem)
   {
      RecordingLog log = new();
      ManagementClient client = new(new FakePipe().Answer(Replies.Text(reply + new string(' ', trailingSpaces))).SendAsync, log);

      ServiceResult<NoResult> result = await client.SharePrinterAsync(Server, "printer-a1", shared: true);

      Assert.Equal((ServiceOutcome.Unavailable, ServiceFailure.UnreadableReply), (result.Outcome, result.Failure));
      Assert.True(result.Unconfirmed);
      Assert.Equal("The service's reply could not be read.", result.Message);
      Assert.Null(result.Reply);
      Assert.Null(result.Payload);
      Assert.Equal($"WARN Management request printers.share on {Server} got a reply that could not be used (UnreadableReply: {problem}).", Assert.Single(log.Entries));
   }



   /// <summary>
   /// The replies of <see cref="UnreadableOrIncompleteReply_IsUnconfirmed_AndTheLogDoesNotQuoteIt"/>, each holding a
   /// mark the log must not show, with the spaces to add after it and the problem the parser names. The last is a
   /// reply that would be read if it were not longer than the contract allows.
   /// </summary>
   public static TheoryData<string, int, string> UnreadableReplies() => new()
   {
      { """["CANARY"]""", 0, "NotAnObject" },
      { """{"v":2,"ok":true,"result":{},"result":{"token":"CANARY"}}""", 0, "RepeatedMember" },
      { """{"v":2,"ok":true,"note":"CANARY"}""", 0, "ReplyIncomplete" },
      { """{"v":2,"ok":false,"error":{"code":"busy"},"note":"CANARY"}""", 0, "ReplyIncomplete" },
      { """{"v":2,"result":{"token":"CANARY"}}""", 0, "ReplyIncomplete" },
      { """{"v":2,"ok":true,"result":{"token":"CANARY"}}""", ManagementProtocol.MaximumReplyCharacters, "TooLarge" },
   };



   /// <summary>A success whose result is not the operation's record is unreadable: the service acted, and what it did is not known here, so the result is unconfirmed. The log names the record and not the result.</summary>
   [Fact]
   public async Task ResultThatDoesNotFitItsType_IsUnreadable()
   {
      RecordingLog log = new();
      ManagementClient client = new(new FakePipe().Answer(Replies.Text("""{"v":2,"ok":true,"result":{"token":"CANARY"}}""")).SendAsync, log);

      ServiceResult<ServerRemoveResult> result = await client.RemoveServerAsync(Server);

      Assert.Equal((ServiceOutcome.Unavailable, ServiceFailure.UnreadableReply), (result.Outcome, result.Failure));
      Assert.True(result.Unconfirmed);
      Assert.Equal("The service's reply could not be read.", result.Message);
      Assert.Null(result.Payload);
      Assert.Equal($"WARN Management request servers.remove on {Server} succeeded, but its result could not be read as ServerRemoveResult.", Assert.Single(log.Entries));
   }



   /// <summary>A failure the pipe client marked as "not sent" (the connect limit, a pipe that could not be opened, an oversize request, an unverified service) says for certain that the service did not act, so there is nothing to check before repeating.</summary>
   [Theory]
   [MemberData(nameof(NotSentFailures))]
   public async Task NothingSent_IsNotUnconfirmed(Exception cause, ServiceFailure failure, string reason)
   {
      RecordingLog log = new();
      AgentPipeNotSentException notSent = new(cause);
      ManagementClient client = new(new FakePipe().Fail(notSent).SendAsync, log);

      ServiceResult<ServerSummary> result = await client.RenameServerAsync(Server, "Warehouse east");

      Assert.Equal((ServiceOutcome.Unavailable, failure), (result.Outcome, result.Failure));
      Assert.False(result.Unconfirmed);
      Assert.Equal(reason, result.Message);
      Assert.Equal(2, log.Entries.Count);
      Assert.Equal($"WARN Management request servers.rename on {Server} did not complete ({failure}, nothing sent): AgentPipeNotSentException: {reason} <- {cause.GetType().Name}: {reason}", log.Entries[0]);
      Assert.StartsWith($"DEBUG Management request servers.rename on {Server} failure detail: {typeof(AgentPipeNotSentException).FullName}", log.Entries[1], StringComparison.Ordinal);
   }



   /// <summary>The causes of <see cref="NothingSent_IsNotUnconfirmed"/>: what the pipe client wrapped, the cause it is, and the reason shown.</summary>
   public static TheoryData<Exception, ServiceFailure, string> NotSentFailures() => new()
   {
      { new Win32Exception(5, "Access is denied."), ServiceFailure.Native, "Access is denied." },
      { new OperationCanceledException("Nothing is listening on the pipe."), ServiceFailure.TimedOut, "Nothing is listening on the pipe." },
      { new IOException("The pipe could not be opened."), ServiceFailure.Disconnected, "The pipe could not be opened." },
      { new InvalidDataException("Local pipe message exceeds the permitted size."), ServiceFailure.InvalidData, "Local pipe message exceeds the permitted size." },
      { new UnauthorizedAccessException("The local endpoint is not the installed Windows print service."), ServiceFailure.AccessDenied, "The local endpoint is not the installed Windows print service." },
   };



   /// <summary>
   /// The same causes without the "not sent" mark are unconfirmed, whatever their type: a refusal or a failed Windows
   /// call that arrives unmarked does not prove that nothing was written, so the operator must check before repeating.
   /// A failure thrown before a task exists is handled the same way as one whose task fails.
   /// </summary>
   [Theory]
   [MemberData(nameof(SentFailures))]
   public async Task FailureAfterSending_IsUnconfirmed(Exception failure, ServiceFailure cause, string reason)
   {
      RecordingLog log = new();
      ManagementClient client = new(new FakePipe().Fail(failure).Throw(failure).SendAsync, log);

      ServiceResult<ServerSummary> result = await client.RenameServerAsync(Server, "Warehouse east");
      ServiceResult<ServerSummary> thrown = await client.RenameServerAsync(OtherServer, "Warehouse east");

      Assert.Equal((ServiceOutcome.Unavailable, cause, true, reason), (result.Outcome, result.Failure, result.Unconfirmed, result.Message));
      Assert.Equal((ServiceOutcome.Unavailable, cause, true, reason), (thrown.Outcome, thrown.Failure, thrown.Unconfirmed, thrown.Message));
      Assert.Null(result.Code);
      Assert.Equal(4, log.Entries.Count);
      Assert.Equal($"WARN Management request servers.rename on {Server} did not complete ({cause}, request sent): {failure.GetType().Name}: {reason}", log.Entries[0]);
      Assert.StartsWith($"DEBUG Management request servers.rename on {Server} failure detail: {failure.GetType().FullName}", log.Entries[1], StringComparison.Ordinal);
      Assert.Equal($"WARN Management request servers.rename on {OtherServer} did not complete ({cause}, request sent): {failure.GetType().Name}: {reason}", log.Entries[2]);
   }



   /// <summary>The failures of <see cref="FailureAfterSending_IsUnconfirmed"/>: the exception, its cause and the reason shown.</summary>
   public static TheoryData<Exception, ServiceFailure, string> SentFailures() => new()
   {
      { new IOException("Service connection closed before a complete response was received."), ServiceFailure.Disconnected, "Service connection closed before a complete response was received." },
      { new OperationCanceledException("The reply did not arrive in time."), ServiceFailure.TimedOut, "The reply did not arrive in time." },
      { new InvalidDataException("Local pipe message exceeds the permitted size."), ServiceFailure.InvalidData, "Local pipe message exceeds the permitted size." },
      { new UnauthorizedAccessException("Access to the path is denied."), ServiceFailure.AccessDenied, "Access to the path is denied." },
      { new Win32Exception(5, "Access is denied."), ServiceFailure.Native, "Access is denied." },
   };



   /// <summary>An exception that is not a pipe failure is a programming error and is not hidden behind a result, whether it comes from the send's task, from the send itself, or wrapped as "not sent"; nothing is logged for it.</summary>
   [Fact]
   public async Task UnrelatedException_IsNotSwallowed()
   {
      RecordingLog log = new();
      FakePipe pipe = new FakePipe().Fail(new FormatException("A bug.")).Throw(new FormatException("A bug thrown at once."))
         .Fail(new AgentPipeNotSentException(new FormatException("A wrapped bug.")));
      ManagementClient client = new(pipe.SendAsync, log);

      FormatException failed = await Assert.ThrowsAsync<FormatException>(() => client.ReconnectAsync(Server));
      FormatException thrown = await Assert.ThrowsAsync<FormatException>(() => client.ReconnectAsync(Server));
      await Assert.ThrowsAsync<AgentPipeNotSentException>(() => client.ReconnectAsync(Server));

      Assert.Equal(("A bug.", "A bug thrown at once."), (failed.Message, thrown.Message));
      Assert.Empty(log.Entries);
   }



   /// <summary>
   /// Cancelling the caller's token ends the wait, not the request, and that is the caller's decision and not a
   /// failure of the service: the result says "canceled" and "unconfirmed", the log has one debug line and no warning,
   /// and the operation is not counted as failing, so the next answer is not announced as a recovery. When the
   /// abandoned request later fails, the failure is observed and noted instead of being left as an unobserved task
   /// exception.
   /// </summary>
   [Fact]
   public async Task CallerCancellation_IsAbandoned_AndALateFailureIsObserved()
   {
      RecordingLog log = new();
      FakePipe pipe = new();
      TaskCompletionSource<ManagementReplyParse> held = pipe.Hold();
      using CancellationTokenSource cancellation = new();
      ManagementClient client = new(pipe.SendAsync, log);

      Task<ServiceResult<JobQueuedResult>> waiting = client.RetryJobAsync(Server, "job-204", confirmDuplicateRisk: false, cancellation.Token);
      await cancellation.CancelAsync();
      ServiceResult<JobQueuedResult> result = await waiting;

      Assert.Equal((ServiceOutcome.Canceled, ServiceFailure.None), (result.Outcome, result.Failure));
      Assert.True(result.Unconfirmed);
      Assert.False(result.Succeeded);
      Assert.Null(result.Code);
      Assert.Equal(string.Empty, result.Message);
      Assert.Equal($"DEBUG Management request jobs.retry on {Server} is no longer waited for; the service may still carry it out.", Assert.Single(log.Entries));

      held.SetException(new IOException("The request failed after the caller stopped waiting."));
      Assert.Equal($"DEBUG Management request jobs.retry on {Server} failed after its wait ended: IOException: The request failed after the caller stopped waiting.", log.Entries[^1]);

      pipe.Answer(Replies.Success(new JobQueuedResult("job-205")));
      Assert.True((await client.RetryJobAsync(Server, "job-204", confirmDuplicateRisk: false)).Succeeded);
      Assert.DoesNotContain(log.Entries, entry => entry.StartsWith("WARN", StringComparison.Ordinal) || entry.Contains("answering again", StringComparison.Ordinal));
   }



   /// <summary>
   /// A token that is already cancelled means the caller left before the request was made. Nothing is sent, so the
   /// service cannot have acted and there is nothing to check: the result is "canceled" and not "unconfirmed". That
   /// holds for a request of its own and for a read that would have joined a shared one.
   /// </summary>
   [Fact]
   public async Task WaitThatHadEndedBeforeTheRequest_SendsNothing()
   {
      RecordingLog log = new();
      FakePipe pipe = new();
      ManagementClient client = new(pipe.SendAsync, log);
      using CancellationTokenSource cancellation = new();
      await cancellation.CancelAsync();

      ServiceResult<EnrollResult> change = await client.EnrollAsync(Server, "code", cancellation.Token);
      ServiceResult<ServiceSnapshot> read = await client.SnapshotAsync(cancellation.Token);

      Assert.Empty(pipe.Sent);
      Assert.Equal((ServiceOutcome.Canceled, false), (change.Outcome, change.Unconfirmed));
      Assert.Equal((ServiceOutcome.Canceled, false), (read.Outcome, read.Unconfirmed));
      Assert.Equal(
      [
         $"DEBUG Management request pairing.enroll on {Server} was not sent: its wait had ended before it was.",
         "DEBUG Management request snapshot on the computer was not sent: its wait had ended before it was.",
      ], log.Entries);
   }



   /// <summary>
   /// The same failure repeating for one operation on one server is a warning once and a debug line afterwards, a
   /// different failure is a warning again, and the first answer after a failure is noted once; a read that fails
   /// every second therefore does not flood the activity log. A refusal is an answer: it is noted as one, and a
   /// failure after it starts over, which the last three steps here show (a refusal, a failure, a result: two notes
   /// of an answer and not one). The same operation on another server is another matter and warns for itself. Every
   /// pipe failure keeps its full exception as debug detail.
   /// </summary>
   [Fact]
   public async Task RepeatedFailure_IsAWarningOnce()
   {
      RecordingLog log = new();
      FakePipe pipe = new FakePipe().Fail(new IOException("down")).Fail(new IOException("down")).Fail(new IOException("down"))
         .Fail(new AgentPipeNotSentException(new UnauthorizedAccessException("refused"))).Answer(Replies.Text("not a reply")).Answer(Replies.Text("still not a reply"))
         .Answer(Replies.Failure(ManagementErrorCode.Busy)).Fail(new IOException("down")).Answer(Replies.Success(new ReconnectResult(ReconnectResult.Requested)));
      ManagementClient client = new(pipe.SendAsync, log);

      _ = await client.ReconnectAsync(Server);
      _ = await client.ReconnectAsync(Server);
      _ = await client.ReconnectAsync(OtherServer);
      for(int attempt = 0; attempt < 6; attempt++)
      {
         _ = await client.ReconnectAsync(Server);
      }

      Assert.Equal(
      [
         $"WARN Management request connection.reconnect on {Server} did not complete (Disconnected, request sent): IOException: down",
         $"DEBUG Management request connection.reconnect on {Server} did not complete (Disconnected, request sent): IOException: down",
         $"WARN Management request connection.reconnect on {OtherServer} did not complete (Disconnected, request sent): IOException: down",
         $"WARN Management request connection.reconnect on {Server} did not complete (AccessDenied, nothing sent): AgentPipeNotSentException: refused <- UnauthorizedAccessException: refused",
         $"WARN Management request connection.reconnect on {Server} got a reply that could not be used (OlderService: NotJson).",
         $"DEBUG Management request connection.reconnect on {Server} got a reply that could not be used (OlderService: NotJson).",
         $"INFO Management request connection.reconnect on {Server} is answering again.",
         $"WARN Management request connection.reconnect on {Server} did not complete (Disconnected, request sent): IOException: down",
         $"INFO Management request connection.reconnect on {Server} is answering again.",
      ], log.Entries.Where(entry => !entry.Contains("failure detail", StringComparison.Ordinal)).ToList());
      Assert.Equal(5, log.Entries.Count(entry => entry.Contains(" failure detail: ", StringComparison.Ordinal)));
   }



   /// <summary>The client needs the pipe's send function and a log.</summary>
   [Fact]
   public void Constructor_RequiresTheSendFunctionAndTheLog()
   {
      Assert.Equal("send", Assert.Throws<ArgumentNullException>(() => new ManagementClient(null!, new RecordingLog())).ParamName);
      Assert.Equal("log", Assert.Throws<ArgumentNullException>(() => new ManagementClient(new FakePipe().SendAsync, null!)).ParamName);
   }
}
