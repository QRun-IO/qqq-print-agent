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

/// <summary>How the typed service client turns reply text and pipe failures into results: one "Error:" rule, one result per failure kind, nothing thrown for the failures the pipe raises, and every swallowed failure written to the log.</summary>
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
      Assert.False(result.Interrupted);
      Assert.Equal("Reconnect requested.", result.Payload);
      Assert.Equal("Reconnect requested.", result.Reply);
      Assert.Equal(string.Empty, result.Message);
      Assert.Equal(ServiceFailure.None, result.Failure);
      Assert.Equal("reconnect", sent?.Operation);
      Assert.Empty(log.Entries);
   }



   /// <summary>The service's error marker is recognised with or without the following space and in any letter case, and the message is the text after it without surrounding spaces; the three spellings the window used to parse differently now give one answer.</summary>
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



   /// <summary>A reply that is not the expected document, or is an empty document, is an unavailable result of the "unreadable" kind with a fixed reason; the log names the exception type only, never the reply text.</summary>
   [Theory]
   [InlineData("not json at all, with a secret-looking token=abc")]
   [InlineData("null")]
   public async Task UnreadableReply_IsUnavailableAndTheLogDoesNotQuoteIt(string reply)
   {
      RecordingLog log = new();
      ServiceClient client = new(_ => Task.FromResult(reply), log, "QQQ");

      ServiceResult<AgentSnapshot> result = await client.SendAsync<AgentSnapshot>(new PipeRequest("snapshot"));

      Assert.Equal(ServiceOutcome.Unavailable, result.Outcome);
      Assert.Equal(ServiceFailure.UnreadableReply, result.Failure);
      Assert.Equal("The service's reply could not be read.", result.Message);
      Assert.Null(result.Reply);
      Assert.Null(result.Payload);
      Assert.Equal(["WARN Service request 'snapshot' did not complete (UnreadableReply): JsonException"], log.Entries);
   }



   /// <summary>Each failure the pipe client raises (the three the window used to catch and the two that used to escape through timer callbacks) becomes an unavailable result of its own kind, carrying the failure's message as the reason, and is logged with its type.</summary>
   [Theory]
   [MemberData(nameof(PipeFailures))]
   public async Task PipeFailure_BecomesAnUnavailableResultOfItsKind(Exception failure, ServiceFailure expected, bool interrupted)
   {
      RecordingLog log = new();
      ServiceClient client = new(_ => Task.FromException<string>(failure), log, "QQQ");

      ServiceResult<string> result = await client.SendAsync(new PipeRequest("transport"));

      Assert.Equal(ServiceOutcome.Unavailable, result.Outcome);
      Assert.Equal(expected, result.Failure);
      Assert.Equal(interrupted, result.Interrupted);
      Assert.Equal(failure.Message, result.Message);
      Assert.Null(result.Reply);
      Assert.Equal([$"WARN Service request 'transport' did not complete ({expected}): {failure.GetType().Name}: {failure.Message}"], log.Entries);
   }



   /// <summary>The failures of <see cref="PipeFailure_BecomesAnUnavailableResultOfItsKind"/>: the exception, the kind it maps to, and whether the request may still have been carried out.</summary>
   public static TheoryData<Exception, ServiceFailure, bool> PipeFailures() => new()
   {
      { new IOException("Service connection closed before a complete response was received."), ServiceFailure.Disconnected, false },
      { new UnauthorizedAccessException("The local endpoint is not the installed Windows print service."), ServiceFailure.AccessDenied, false },
      { new OperationCanceledException("The pipe time limit ran out."), ServiceFailure.TimedOut, true },
      { new Win32Exception(5, "Access is denied."), ServiceFailure.Native, false },
      { new InvalidDataException("Local pipe message exceeds the permitted size."), ServiceFailure.InvalidData, false },
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



   /// <summary>An exception that is not a pipe failure is a programming error and is not hidden behind a result.</summary>
   [Fact]
   public async Task UnexpectedException_IsNotSwallowed()
   {
      RecordingLog log = new();
      ServiceClient client = new(_ => Task.FromException<string>(new InvalidOperationException("A bug.")), log, "QQQ");

      InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(new PipeRequest("snapshot")));

      Assert.Equal("A bug.", thrown.Message);
      Assert.Empty(log.Entries);
   }



   /// <summary>The same failure repeating for one operation is logged as a warning once and as debug detail afterwards, a different failure is a warning again, and the first answer after a failure is noted once; a status poll that fails every second therefore does not flood the activity log.</summary>
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
         "WARN Service request 'reconnect' did not complete (Disconnected): IOException: down",
         "DEBUG Service request 'reconnect' did not complete (Disconnected): IOException: down",
         "WARN Service request 'reconnect' did not complete (AccessDenied): UnauthorizedAccessException: refused",
         "INFO Service request 'reconnect' is answering again.",
      ], log.Entries);
   }



   /// <summary>Cancelling the caller's token ends the wait with a "canceled" result while the request itself is still running; when that request later fails, the failure is observed rather than left as an unobserved task exception.</summary>
   [Fact]
   public async Task CallerCancellation_EndsTheWaitAndObservesTheAbandonedRequest()
   {
      TaskCompletionSource<string> pending = new();
      using CancellationTokenSource cancellation = new();
      ServiceClient client = new(_ => pending.Task, new RecordingLog(), "QQQ");

      Task<ServiceResult<string>> waiting = client.SendAsync(new PipeRequest("retry-job"), cancellation.Token);
      await cancellation.CancelAsync();
      ServiceResult<string> result = await waiting;
      pending.SetException(new IOException("The request failed after the caller stopped waiting."));

      Assert.Equal(ServiceOutcome.Unavailable, result.Outcome);
      Assert.Equal(ServiceFailure.Canceled, result.Failure);
      Assert.True(result.Interrupted);
      Assert.True(pending.Task.IsFaulted);
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
