/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Service;

/// <summary>
/// What the version 2 client must never write to its log: a session credential, an enrollment code, the identity
/// provider's sign-out request, or any other argument of any request, and nothing a reply held. Every operation is
/// called with values that carry a mark, through every way a request can end, against a log that keeps the values it
/// is handed as well as the text. The mark must be in no entry, and nothing but plain text, a number or a named
/// value may be handed over: an object would be kept by a host that keeps values, however harmless its text form.
/// </summary>
public sealed class ManagementClientSecrecyTests
{
   /// <summary>The server the requests that fill the ceiling name.</summary>
   private const string Server = "3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f";



   /// <summary>
   /// Each operation, twice in a row, through each thing the pipe can do with it: answer, refuse with a known and with
   /// an unknown code, fail after sending, fail before sending and then answer, answer in another version or in none,
   /// and answer with a reply or a result that cannot be read. The replies carry the mark too, in messages, codes,
   /// details and results, and the golden sign-out reply carries the provider's ID-token hint.
   /// </summary>
   [Theory]
   [MemberData(nameof(CompanionCalls.Sent), MemberType = typeof(CompanionCalls))]
   public async Task CredentialCodeAndSignOutRequest_ReachNoLogLine(string operation)
   {
      CompanionCall call = CompanionCalls.For(operation);
      foreach((string world, bool logs, FakePipe pipe) in Worlds(operation))
      {
         TemplateLog log = new();
         ManagementClient client = new(pipe.SendAsync, log);

         _ = await call.Send(client, SampleValues.Marked, CancellationToken.None);
         _ = await call.Send(client, SampleValues.Marked, CancellationToken.None);

         Assert.Equal(2, pipe.Sent.Count);
         Assert.True(!logs || log.Entries.Count > 0, world);
         AssertNothingGivenWasWritten(log, world);
      }
   }



   /// <summary>The same when the caller stops waiting: before the request is made, and while it is in flight, after which the request fails late.</summary>
   [Theory]
   [MemberData(nameof(CompanionCalls.Sent), MemberType = typeof(CompanionCalls))]
   public async Task CredentialCodeAndSignOutRequest_ReachNoLogLine_WhenTheCallerStopsWaiting(string operation)
   {
      CompanionCall call = CompanionCalls.For(operation);
      TemplateLog log = new();
      FakePipe pipe = new();
      TaskCompletionSource<ManagementReplyParse> held = pipe.Hold();
      ManagementClient client = new(pipe.SendAsync, log);
      using CancellationTokenSource leaving = new();

      Task<CallResult> inFlight = call.Send(client, SampleValues.Marked, leaving.Token);
      await leaving.CancelAsync();
      CallResult abandoned = await inFlight;
      held.SetException(new IOException("The pipe closed."));
      CallResult notStarted = await call.Send(client, SampleValues.Marked, leaving.Token);

      Assert.Equal((ServiceOutcome.Canceled, true), (abandoned.Outcome, abandoned.Unconfirmed));
      Assert.Equal((ServiceOutcome.Canceled, false), (notStarted.Outcome, notStarted.Unconfirmed));
      Assert.True(log.Entries.Count >= 3, "The abandoned request, its late failure and the request that was not started are each noted.");
      AssertNothingGivenWasWritten(log, "the caller stops waiting");
   }



   /// <summary>The same at the ceiling, where a repeated read is skipped and every other request waits its turn until its caller stops waiting for it.</summary>
   [Theory]
   [MemberData(nameof(CompanionCalls.Sent), MemberType = typeof(CompanionCalls))]
   public async Task CredentialCodeAndSignOutRequest_ReachNoLogLine_AtTheCeiling(string operation)
   {
      CompanionCall call = CompanionCalls.For(operation);
      TemplateLog log = new();
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient client = new(pipe.SendAsync, log);
      using CancellationTokenSource leaving = new();
      for(int index = 0; index < ManagementClient.MaximumRequestsInFlight; index++)
      {
         _ = client.AcknowledgeJobAsync(Server, "job-" + index);
      }

      Task<CallResult> overTheCeiling = call.Send(client, SampleValues.Marked, leaving.Token);
      await leaving.CancelAsync();
      CallResult result = await overTheCeiling;

      Assert.Contains(result.Outcome, new[] { ServiceOutcome.Skipped, ServiceOutcome.Canceled });
      Assert.False(result.Unconfirmed);
      Assert.Equal(ManagementClient.MaximumRequestsInFlight, pipe.Sent.Count);
      Assert.NotEmpty(log.Entries);
      AssertNothingGivenWasWritten(log, "at the ceiling");
   }



   /// <summary>
   /// The provider's sign-out request reaches its caller in the result, which is the one place it is for, and a
   /// result keeps no reply text beside it. The session credential and the enrollment code reach the pipe in the
   /// request, which is the one place they are for.
   /// </summary>
   [Fact]
   public async Task SecretsGoWhereTheyAreFor_AndNowhereElse()
   {
      TemplateLog log = new();
      FakePipe pipe = new FakePipe().Answer(Replies.Golden("session.signOut.reply.json")).Answer(Replies.Golden("servers.add.reply.json")).Answer(Replies.Golden("pairing.enroll.reply.json"));
      ManagementClient client = new(pipe.SendAsync, log);
      SampleValues given = SampleValues.Marked;

      ServiceResult<SignOutResult> signedOut = await client.SignOutAsync(Server, given.CallbackAddress);
      _ = await client.AddServerAsync(given.Address, given.Name, acceptCertificateException: true, given.ProviderOrigin, given.Credential);
      _ = await client.EnrollAsync(Server, given.EnrollmentCode);

      Assert.Contains("CANARY-ID-TOKEN-HINT-08be", signedOut.Payload!.ProviderLogout!.EndSessionUri.Query, StringComparison.Ordinal);
      Assert.Null(signedOut.Reply);
      Assert.DoesNotContain(SampleValues.Mark, signedOut.Payload.ToString(), StringComparison.OrdinalIgnoreCase);
      Assert.Contains("\"sessionToken\":\"CANARY-SESSION-TOKEN\"", pipe.Wire(1), StringComparison.Ordinal);
      Assert.Contains("\"enrollmentCode\":\"CANARY-ENROLLMENT-CODE\"", pipe.Wire(2), StringComparison.Ordinal);
      Assert.Empty(log.Entries);
   }



   /// <summary>What the pipe does with two requests in a row, by name, and whether the client must have logged something.</summary>
   private static IEnumerable<(string World, bool Logs, FakePipe Pipe)> Worlds(string operation)
   {
      ManagementReplyParse answer = Replies.Golden(operation + ".reply.json");
      yield return ("answered", false, new FakePipe().Answer(answer).Answer(answer));
      yield return ("refused, with a known code and with an unknown one", true, new FakePipe().Answer(Replies.Failure(ManagementErrorCode.Busy, "CANARY message"))
         .Answer(Replies.Text("""{"v":2,"ok":false,"error":{"code":"CANARY code","message":"CANARY message","details":{"token":"CANARY"}}}""")));
      yield return ("failed after sending, twice", true, new FakePipe().Fail(new IOException("The pipe closed.")).Fail(new IOException("The pipe closed.")));
      yield return ("failed before sending, then answered", true, new FakePipe().Throw(new AgentPipeNotSentException(new IOException("The pipe could not be opened."))).Answer(answer));
      yield return ("answered by an older and by a newer service", true, new FakePipe().Answer(Replies.Text("Error: CANARY")).Answer(Replies.Text("""{"v":3,"note":"CANARY"}""")));
      yield return ("answered unreadably", true, new FakePipe().Answer(Replies.Text("""{"v":2,"ok":true,"note":"CANARY"}"""))
         .Answer(Replies.Text("""{"v":2,"ok":true,"result":{"servers":"CANARY","job":"CANARY","state":7}}""")));
   }



   /// <summary>Requires that no entry holds the mark, and that every value handed to the log is plain text, a number or a named value.</summary>
   private static void AssertNothingGivenWasWritten(TemplateLog log, string world)
   {
      Assert.DoesNotContain(log.Entries, entry => entry.Contains(SampleValues.Mark, StringComparison.OrdinalIgnoreCase));
      Assert.All(log.Values, value => Assert.True(value is string or int or Enum, $"{world}: a {value?.GetType().Name} was handed to the log."));
   }
}
