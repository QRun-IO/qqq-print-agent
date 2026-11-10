/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.Service;

/// <summary>How the typed service client turns reply text and pipe failures into results: one "Error:" rule, one result per failure with its cause and whether the request had left, nothing thrown for the failures the pipe raises, and every swallowed failure written to the log.</summary>
public sealed class ServiceClientTests
{
   /// <summary>A plain reply to a text request is a success whose payload and raw reply are that text, with nothing logged.</summary>
   [Fact]
   public async Task TextReply_IsSuccessWithTheReplyAsPayload()
   {
      RecordingLog log = new();
      PipeRequest? sent = null;
      ServiceClient client = new(request => { sent = request; return Task.FromResult("Reconnect requested."); }, log, "QQQ");

      ServiceResult<string> result = await client.SendAsync(new PipeRequest("reconnect"));

      Assert.Equal(ServiceOutcome.Success, result.Outcome);
      Assert.True(result.Succeeded);
      Assert.False(result.Unconfirmed);
      Assert.Equal("Reconnect requested.", result.Payload);
      Assert.Equal("Reconnect requested.", result.Reply);
      Assert.Equal(string.Empty, result.Message);
      Assert.Equal(ServiceFailure.None, result.Failure);
      Assert.Equal("reconnect", sent?.Operation);
      Assert.Empty(log.Entries);
   }



   /// <summary>The service's error marker is recognised with or without the following space and in any letter case, and the message is the text after it without surrounding spaces; the three spellings the window used to parse differently now give one answer. The service answered, so nothing is unconfirmed.</summary>
   [Theory]
   [InlineData("Error: The printer is not shared.")]
   [InlineData("Error:The printer is not shared.")]
   [InlineData("ERROR:   The printer is not shared.  ")]
   public async Task ErrorMarker_IsARejectionCarryingTheMessageAndTheWholeReply(string reply)
   {
      ServiceClient client = new(_ => Task.FromResult(reply), new RecordingLog(), "QQQ");

      ServiceResult<string> text = await client.SendAsync(new PipeRequest("retry-job"));
      ServiceResult<JobPage> typed = await client.SendAsync<JobPage>(new PipeRequest("query-jobs"));

      Assert.Equal(ServiceOutcome.Rejected, text.Outcome);
      Assert.False(text.Succeeded);
      Assert.False(text.Unconfirmed);
      Assert.Null(text.Payload);
      Assert.Equal("The printer is not shared.", text.Message);
      Assert.Equal(reply, text.Reply);
      Assert.Equal(ServiceFailure.None, text.Failure);
      Assert.Equal(ServiceOutcome.Rejected, typed.Outcome);
      Assert.Null(typed.Payload);
      Assert.Equal("The printer is not shared.", typed.Message);
   }



   /// <summary>A JSON reply is read with the options the service writes with (camel-case names), giving the typed payload.</summary>
   [Fact]
   public async Task JsonReply_IsReadIntoTheTypedPayload()
   {
      JobPage page = new([new JobSnapshot("job-1", "QUEUED", "2026-11-10T10:00:00Z", null, null)], TotalCount: 61, Offset: 50, PageSize: 50, ServerQueueLimited: true);
      ServiceClient client = new(_ => Task.FromResult(JsonSerializer.Serialize(page, PrintApiClient.JsonOptions)), new RecordingLog(), "QQQ");

      ServiceResult<JobPage> result = await client.SendAsync<JobPage>(new PipeRequest("query-jobs"));

      Assert.True(result.Succeeded);
      Assert.Equal(61, result.Payload.TotalCount);
      Assert.Equal(50, result.Payload.Offset);
      Assert.True(result.Payload.ServerQueueLimited);
      Assert.Equal("job-1", Assert.Single(result.Payload.Jobs).JobId);
   }



   /// <summary>A reply that is not the expected document, or is an empty document, is an unavailable result of the "unreadable" kind with a fixed reason. The request was carried out, so it is unconfirmed, and the log names the exception type only, never the reply text, not even as debug detail.</summary>
   [Theory]
   [InlineData("not json at all, with a secret-looking token=abc")]
   [InlineData("null")]
   public async Task UnreadableReply_IsUnconfirmedAndTheLogDoesNotQuoteIt(string reply)
   {
      RecordingLog log = new();
      ServiceClient client = new(_ => Task.FromResult(reply), log, "QQQ");

      ServiceResult<AgentSnapshot> result = await client.SendAsync<AgentSnapshot>(new PipeRequest("snapshot"));

      Assert.Equal(ServiceOutcome.Unavailable, result.Outcome);
      Assert.Equal(ServiceFailure.UnreadableReply, result.Failure);
      Assert.True(result.Unconfirmed);
      Assert.Equal("The service's reply could not be read.", result.Message);
      Assert.Null(result.Reply);
      Assert.Null(result.Payload);
      Assert.Equal("WARN Service request 'snapshot' did not complete (UnreadableReply, request sent): JsonException", Assert.Single(log.Entries));
   }



   /// <summary>
   /// Each failure the pipe client raises becomes an unavailable result with its cause and, separately, whether the request
   /// had left. A failure the pipe client marked as "not sent" (the connect limit, a pipe that could not be opened, an
   /// oversize request, an unverified service) is never unconfirmed; the same cause after the request left is. So a connect
   /// time limit and a reply time limit share a cause and differ in what the operator must do. The failure's own message is
   /// the reason, the first failure is a warning, and the full exception follows as debug detail.
   /// </summary>
   [Theory]
   [MemberData(nameof(PipeFailures))]
   public async Task PipeFailure_BecomesAResultWithItsCauseAndWhetherTheRequestLeft(Exception failure, ServiceFailure cause, bool sent, string reason)
   {
      RecordingLog log = new();
      ServiceClient client = new(_ => Task.FromException<string>(failure), log, "QQQ");

      ServiceResult<string> result = await client.SendAsync(new PipeRequest("transport"));

      Assert.Equal(ServiceOutcome.Unavailable, result.Outcome);
      Assert.Equal(cause, result.Failure);
      Assert.Equal(sent, result.Unconfirmed);
      Assert.Equal(reason, result.Message);
      Assert.Null(result.Reply);
      Assert.Equal(2, log.Entries.Count);
      Assert.StartsWith($"WARN Service request 'transport' did not complete ({cause}, {(sent ? "request sent" : "nothing sent")}): {failure.GetType().Name}: {reason}", log.Entries[0], StringComparison.Ordinal);
      Assert.StartsWith("DEBUG Service request 'transport' failure detail: " + failure.GetType().FullName, log.Entries[1], StringComparison.Ordinal);
   }



   /// <summary>The failures of <see cref="PipeFailure_BecomesAResultWithItsCauseAndWhetherTheRequestLeft"/>: the exception, its cause, whether the request had left, and the reason shown.</summary>
   public static TheoryData<Exception, ServiceFailure, bool, string> PipeFailures() => new()
   {
      { new IOException("Service connection closed before a complete response was received."), ServiceFailure.Disconnected, true, "Service connection closed before a complete response was received." },
      { new OperationCanceledException("The reply did not arrive in time."), ServiceFailure.TimedOut, true, "The reply did not arrive in time." },
      { new InvalidDataException("Local pipe message exceeds the permitted size."), ServiceFailure.InvalidData, true, "Local pipe message exceeds the permitted size." },
      { new UnauthorizedAccessException("The local endpoint is not the installed Windows print service."), ServiceFailure.AccessDenied, false, "The local endpoint is not the installed Windows print service." },
      { new Win32Exception(5, "Access is denied."), ServiceFailure.Native, false, "Access is denied." },
      { new AgentPipeNotSentException(new OperationCanceledException("Nothing is listening on the pipe.")), ServiceFailure.TimedOut, false, "Nothing is listening on the pipe." },
      { new AgentPipeNotSentException(new IOException("The pipe could not be opened.")), ServiceFailure.Disconnected, false, "The pipe could not be opened." },
      { new AgentPipeNotSentException(new InvalidDataException("Local pipe message exceeds the permitted size.")), ServiceFailure.InvalidData, false, "Local pipe message exceeds the permitted size." },
      { new AgentPipeNotSentException(new UnauthorizedAccessException("The local endpoint is not the installed Windows print service.")), ServiceFailure.AccessDenied, false, "The local endpoint is not the installed Windows print service." },
   };



   /// <summary>A send delegate that throws before it returns a task is handled the same way as one whose task fails.</summary>
   [Fact]
   public async Task FailureThrownBeforeATaskExists_IsStillAResult()
   {
      ServiceClient client = new(_ => throw new IOException("The pipe could not be opened."), new RecordingLog(), "QQQ");

      ServiceResult<AgentSnapshot> result = await client.SnapshotAsync();

      Assert.Equal(ServiceFailure.Disconnected, result.Failure);
      Assert.Equal("The pipe could not be opened.", result.Message);
   }



   /// <summary>An exception that is not a pipe failure is a programming error and is not hidden behind a result, whether it comes straight from the send or wrapped as "not sent"; nothing is logged for it.</summary>
   [Fact]
   public async Task UnrelatedException_IsNotSwallowed()
   {
      RecordingLog log = new();
      ServiceClient direct = new(_ => Task.FromException<string>(new FormatException("A bug.")), log, "QQQ");
      ServiceClient wrapped = new(_ => Task.FromException<string>(new AgentPipeNotSentException(new FormatException("A wrapped bug."))), log, "QQQ");

      FormatException thrown = await Assert.ThrowsAsync<FormatException>(() => direct.SendAsync(new PipeRequest("snapshot")));
      await Assert.ThrowsAsync<AgentPipeNotSentException>(() => wrapped.SendAsync(new PipeRequest("snapshot")));

      Assert.Equal("A bug.", thrown.Message);
      Assert.Empty(log.Entries);
   }



   /// <summary>The same failure repeating for one operation is a warning once and one debug line afterwards, a different failure is a warning again, and the first answer after a failure is noted once; a status poll that fails every second therefore does not flood the activity log. Every failure keeps its full exception as debug detail.</summary>
   [Fact]
   public async Task RepeatedFailure_IsLoggedOnceAsAWarningUntilTheServiceAnswersAgain()
   {
      RecordingLog log = new();
      Queue<Func<Task<string>>> replies = new(
      [
         () => Task.FromException<string>(new IOException("down")),
         () => Task.FromException<string>(new IOException("down")),
         () => Task.FromException<string>(new UnauthorizedAccessException("refused")),
         () => Task.FromResult("Error: still starting"),
         () => Task.FromResult("ok"),
      ]);
      ServiceClient client = new(_ => replies.Dequeue()(), log, "QQQ");

      for(int attempt = 0; attempt < 5; attempt++)
      {
         _ = await client.SendAsync(new PipeRequest("reconnect"));
      }

      Assert.Equal(
      [
         "WARN Service request 'reconnect' did not complete (Disconnected, request sent): IOException: down",
         "DEBUG Service request 'reconnect' did not complete (Disconnected, request sent): IOException: down",
         "WARN Service request 'reconnect' did not complete (AccessDenied, nothing sent): UnauthorizedAccessException: refused",
         "INFO Service request 'reconnect' is answering again.",
      ], log.Entries.Where(entry => !entry.Contains("failure detail", StringComparison.Ordinal)).ToList());
      Assert.Equal(3, log.Entries.Count(entry => entry.StartsWith("DEBUG Service request 'reconnect' failure detail: System.", StringComparison.Ordinal)));
   }



   /// <summary>
   /// Cancelling the caller's token ends the wait, not the request, and that is the caller's decision rather than a failure of
   /// the service: the result says "canceled" and "unconfirmed", the log has one debug line and no warning, and the operation
   /// is not counted as failing, so the next answer is not announced as a recovery. When the abandoned request later fails,
   /// the failure is observed and noted instead of being left as an unobserved task exception.
   /// </summary>
   [Fact]
   public async Task CallerCancellation_IsNotAServiceFailureAndTheAbandonedRequestIsObserved()
   {
      RecordingLog log = new();
      TaskCompletionSource<string> pending = new();
      Func<Task<string>> next = () => pending.Task;
      using CancellationTokenSource cancellation = new();
      ServiceClient client = new(_ => next(), log, "QQQ");

      Task<ServiceResult<string>> waiting = client.SendAsync(new PipeRequest("retry-job"), cancellation.Token);
      await cancellation.CancelAsync();
      ServiceResult<string> result = await waiting;

      Assert.Equal(ServiceOutcome.Canceled, result.Outcome);
      Assert.Equal(ServiceFailure.None, result.Failure);
      Assert.True(result.Unconfirmed);
      Assert.False(result.Succeeded);
      Assert.Equal(string.Empty, result.Message);
      Assert.Equal("DEBUG Service request 'retry-job' is no longer waited for; the service may still carry it out.", Assert.Single(log.Entries));

      pending.SetException(new IOException("The request failed after the caller stopped waiting."));
      Assert.Equal("DEBUG Service request 'retry-job' failed after its wait ended: IOException: The request failed after the caller stopped waiting.", log.Entries[^1]);

      next = () => Task.FromResult("ok");
      Assert.True((await client.SendAsync(new PipeRequest("retry-job"))).Succeeded);
      next = () => Task.FromCanceled<string>(cancellation.Token);
      Assert.Equal(ServiceOutcome.Canceled, (await client.SendAsync(new PipeRequest("retry-job"), cancellation.Token)).Outcome);
      Assert.DoesNotContain(log.Entries, entry => entry.StartsWith("WARN", StringComparison.Ordinal) || entry.Contains("answering again", StringComparison.Ordinal));
   }



   /// <summary>A wait that ends because the pipe's own limit ran out is still a failure when the caller's token was not the one that asked for it.</summary>
   [Fact]
   public async Task PipeTimeLimitWhileATokenIsSupplied_IsATimeoutNotACancellation()
   {
      using CancellationTokenSource cancellation = new();
      ServiceClient client = new(_ => Task.FromException<string>(new OperationCanceledException("The reply did not arrive in time.")), new RecordingLog(), "QQQ");

      ServiceResult<string> result = await client.SendAsync(new PipeRequest("retry-job"), cancellation.Token);

      Assert.Equal(ServiceOutcome.Unavailable, result.Outcome);
      Assert.Equal(ServiceFailure.TimedOut, result.Failure);
   }



   /// <summary>The client refuses to be built without its send delegate, log or server name, and refuses a missing request, so a wiring mistake fails at once instead of on the first click.</summary>
   [Fact]
   public async Task MissingCollaborators_AreRefused()
   {
      RecordingLog log = new();
      Func<PipeRequest, Task<string>> send = _ => Task.FromResult("ok");

      Assert.Throws<ArgumentNullException>(() => new ServiceClient(null!, log, "QQQ"));
      Assert.Throws<ArgumentNullException>(() => new ServiceClient(send, null!, "QQQ"));
      Assert.Throws<ArgumentNullException>(() => new ServiceClient(send, log, null!));
      await Assert.ThrowsAsync<ArgumentNullException>(() => new ServiceClient(send, log, "QQQ").SendAsync(null!));
   }
}
