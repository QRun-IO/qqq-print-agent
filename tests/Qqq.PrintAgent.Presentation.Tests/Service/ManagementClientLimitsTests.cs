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
/// The two limits the version 2 client puts on itself: at most twelve requests in flight, and one snapshot and one
/// transport read at a time. Each test decides the order of events itself: the pipe holds every request until the
/// test answers it, a request that is sent at once is sent before the call returns, and a request that waited its
/// turn is awaited by its arrival at the pipe. Nothing here sleeps, and no time passes that a test depends on.
/// </summary>
public sealed class ManagementClientLimitsTests
{
   /// <summary>The server the requests of these tests name.</summary>
   private const string Server = "3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f";



   /// <summary>How long a test waits for something that must happen on another thread before it fails instead of hanging the run. It is a guard, never what a test asserts.</summary>
   private static readonly TimeSpan s_guard = TimeSpan.FromSeconds(30);



   /// <summary>The ceiling is twelve, which leaves four of the pipe's sixteen instances to the command line, the update helper and a second Windows session.</summary>
   [Fact]
   public void Ceiling_IsTwelve_AndLeavesFourOfThePipesInstances()
   {
      Assert.Equal(12, ManagementClient.MaximumRequestsInFlight);
      Assert.Equal(4, ManagementProtocol.MaximumConcurrentClients - ManagementClient.MaximumRequestsInFlight);
   }



   /// <summary>
   /// With twelve requests in flight, each of the three reads the window repeats on a timer is skipped: it is not
   /// sent, its result says so and that nothing is to be checked, and it is not a failure. A change waits, and so
   /// does a read an operator asked for once; they are sent in the order they came, each when a request in flight is
   /// done, and are then answered like any other.
   /// </summary>
   [Fact]
   public async Task TwelveInFlight_AReadIsSkipped_AChangeWaits()
   {
      RecordingLog log = new();
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient client = new(pipe.SendAsync, log);
      Task<ServiceResult<NoResult>>[] inFlight = [.. Enumerable.Range(0, 12).Select(index => client.AcknowledgeJobAsync(Server, "job-" + index))];
      Assert.Equal(12, pipe.Sent.Count);

      ServiceResult<ServiceSnapshot> snapshot = await AtOnce(client.SnapshotAsync());
      ServiceResult<TransportResult> transport = await AtOnce(client.TransportAsync());
      ServiceResult<JobPage> jobs = await AtOnce(client.QueryJobsAsync(Server, new JobQuery()));
      Task<ServiceResult<ReconnectResult>> change = client.ReconnectAsync(Server);
      Task<ServiceResult<TrustResult>> read = client.GetTrustAsync(Server);

      Assert.Equal((ServiceOutcome.Skipped, false, ServiceFailure.None, string.Empty), (snapshot.Outcome, snapshot.Unconfirmed, snapshot.Failure, snapshot.Message));
      Assert.False(snapshot.Succeeded);
      Assert.Equal((ServiceOutcome.Skipped, false), (transport.Outcome, transport.Unconfirmed));
      Assert.Equal((ServiceOutcome.Skipped, false), (jobs.Outcome, jobs.Unconfirmed));
      Assert.False(change.IsCompleted || read.IsCompleted);
      Assert.Equal(12, pipe.Sent.Count);

      pipe.Held[0].SetResult(Replies.Success(new NoResult()));
      Assert.True((await inFlight[0]).Succeeded);
      await pipe.Arrived(13);
      Assert.Equal(ManagementOperations.ConnectionReconnect, pipe.Sent[12].Op);
      Assert.Equal(13, pipe.Sent.Count);
      pipe.Held[1].SetResult(Replies.Success(new NoResult()));
      await pipe.Arrived(14);
      Assert.Equal(ManagementOperations.TrustGet, pipe.Sent[13].Op);
      pipe.Held[12].SetResult(Replies.Success(new ReconnectResult(ReconnectResult.Requested)));
      pipe.Held[13].SetResult(Replies.Golden("trust.get.reply.json"));

      Assert.Equal(ReconnectResult.Requested, (await change).Payload!.Outcome);
      Assert.True((await read).Succeeded);
      Assert.Equal(
      [
         "DEBUG Management request snapshot on the computer was skipped: 12 requests are already in flight. It is asked again on its next tick.",
         "DEBUG Management request transport.get on the computer was skipped: 12 requests are already in flight. It is asked again on its next tick.",
         $"DEBUG Management request jobs.query on {Server} was skipped: 12 requests are already in flight. It is asked again on its next tick.",
         $"DEBUG Management request connection.reconnect on {Server} waits its turn: 12 requests are already in flight.",
         $"DEBUG Management request trust.get on {Server} waits its turn: 12 requests are already in flight.",
      ], log.Entries);
   }



   /// <summary>
   /// A change whose caller stops waiting while it waits its turn is never sent: nobody waits for it any more, so it
   /// must not be made later. Its result says that nothing was sent. Its turn passes to the next in line.
   /// </summary>
   [Fact]
   public async Task ChangeWhoseCallerLeavesWhileItWaitsItsTurn_IsNeverSent_AndTheNextInLineGoes()
   {
      RecordingLog log = new();
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient client = new(pipe.SendAsync, log);
      using CancellationTokenSource leaving = new();
      for(int index = 0; index < 12; index++)
      {
         _ = client.AcknowledgeJobAsync(Server, "job-" + index);
      }

      Task<ServiceResult<ServerRemoveResult>> left = client.RemoveServerAsync(Server, leaving.Token);
      Task<ServiceResult<ReconnectResult>> next = client.ReconnectAsync(Server);
      await leaving.CancelAsync();
      ServiceResult<ServerRemoveResult> result = await left.WaitAsync(s_guard);
      pipe.Held[0].SetResult(Replies.Success(new NoResult()));
      await pipe.Arrived(13);
      pipe.Held[12].SetResult(Replies.Success(new ReconnectResult(ReconnectResult.Requested)));

      Assert.Equal((ServiceOutcome.Canceled, false), (result.Outcome, result.Unconfirmed));
      Assert.True((await next).Succeeded);
      Assert.Equal(13, pipe.Sent.Count);
      Assert.DoesNotContain(pipe.Sent, request => string.Equals(request.Op, ManagementOperations.ServersRemove, StringComparison.Ordinal));
      Assert.Contains($"DEBUG Management request servers.remove on {Server} was not sent: its wait had ended before it was.", log.Entries, StringComparer.Ordinal);
   }



   /// <summary>
   /// A request is in flight until the pipe is done with it, not until its caller stops waiting: it occupies one of
   /// the pipe's instances for that long. So a request nobody waits for any more still counts, and its place is free
   /// only when the pipe answers or fails it.
   /// </summary>
   [Fact]
   public async Task RequestNobodyWaitsFor_KeepsItsPlaceUntilThePipeIsDoneWithIt()
   {
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());
      using CancellationTokenSource leaving = new();
      Task<ServiceResult<NoResult>> abandoned = client.AcknowledgeJobAsync(Server, "job-0", leaving.Token);
      for(int index = 1; index < 12; index++)
      {
         _ = client.AcknowledgeJobAsync(Server, "job-" + index);
      }

      await leaving.CancelAsync();
      ServiceResult<NoResult> left = await abandoned;
      ServiceResult<JobPage> whileHeld = await AtOnce(client.QueryJobsAsync(Server, new JobQuery()));
      pipe.Held[0].SetException(new IOException("The pipe closed."));
      Task<ServiceResult<JobPage>> afterwards = client.QueryJobsAsync(Server, new JobQuery());

      Assert.Equal((ServiceOutcome.Canceled, true), (left.Outcome, left.Unconfirmed));
      Assert.Equal(ServiceOutcome.Skipped, whileHeld.Outcome);
      Assert.Equal(13, pipe.Sent.Count);
      Assert.Equal(ManagementOperations.JobsQuery, pipe.Sent[12].Op);
      Assert.False(afterwards.IsCompleted);
   }



   /// <summary>A request that fails gives its place back like one that is answered, whether the send failed at once or its task did, so a service that is down never uses the places up.</summary>
   [Fact]
   public async Task RequestThatFails_GivesItsPlaceBack()
   {
      FakePipe pipe = new();
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());
      for(int index = 0; index < 12; index++)
      {
         _ = index % 2 == 0 ? pipe.Throw(new IOException("down")) : pipe.Fail(new AgentPipeNotSentException(new IOException("down")));
         Assert.Equal(ServiceFailure.Disconnected, (await client.AcknowledgeJobAsync(Server, "job-" + index)).Failure);
      }
      pipe.Answer(Replies.Golden("transport.get.reply.json"));

      ServiceResult<TransportResult> read = await AtOnce(client.TransportAsync());

      Assert.True(read.Succeeded);
      Assert.Equal(13, pipe.Sent.Count);
   }



   /// <summary>
   /// While a snapshot is in flight, another call for one sends nothing and shares its result; the same holds for the
   /// transport read, and the two do not share with each other. Once the flight is over, the next call starts a new
   /// one. A flight that fails is shared in the same way, and is one failure in the log, not one for each caller.
   /// </summary>
   [Fact]
   public async Task SnapshotAndTransport_AreSingleFlight()
   {
      RecordingLog log = new();
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient client = new(pipe.SendAsync, log);

      Task<ServiceResult<ServiceSnapshot>> first = client.SnapshotAsync();
      Task<ServiceResult<ServiceSnapshot>> second = client.SnapshotAsync();
      Task<ServiceResult<TransportResult>> meter = client.TransportAsync();
      Task<ServiceResult<TransportResult>> meterAgain = client.TransportAsync();
      Assert.Equal([ManagementOperations.Snapshot, ManagementOperations.TransportGet], pipe.Sent.Select(request => request.Op), StringComparer.Ordinal);
      Assert.False(first.IsCompleted || second.IsCompleted || meter.IsCompleted || meterAgain.IsCompleted);

      pipe.Held[0].SetResult(Replies.Golden("snapshot.healthy.reply.json"));
      pipe.Held[1].SetException(new IOException("down"));
      ServiceResult<ServiceSnapshot> shared = await first;

      Assert.True(shared.Succeeded);
      Assert.Equal(2, shared.Payload.Servers.Count);
      Assert.Same(shared, await second);
      Assert.Equal(ServiceFailure.Disconnected, (await meter).Failure);
      Assert.Same(await meter, await meterAgain);
      Assert.Single(log.Entries, entry => entry.StartsWith("WARN", StringComparison.Ordinal));

      Task<ServiceResult<ServiceSnapshot>> later = client.SnapshotAsync();
      Task<ServiceResult<TransportResult>> meterLater = client.TransportAsync();
      Assert.Equal(4, pipe.Sent.Count);
      Assert.False(later.IsCompleted || meterLater.IsCompleted);
   }



   /// <summary>
   /// A shared read belongs to no caller. One caller ending its wait gets "canceled" and leaves the read in flight for
   /// the others, who get its answer; and a read that fails after everyone left is still observed.
   /// </summary>
   [Fact]
   public async Task SharedRead_OneCallerLeaving_LeavesTheOthersTheirAnswer()
   {
      RecordingLog log = new();
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient client = new(pipe.SendAsync, log);
      using CancellationTokenSource leaving = new();

      Task<ServiceResult<ServiceSnapshot>> leaver = client.SnapshotAsync(leaving.Token);
      Task<ServiceResult<ServiceSnapshot>> stayer = client.SnapshotAsync();
      await leaving.CancelAsync();
      ServiceResult<ServiceSnapshot> left = await leaver.WaitAsync(s_guard);
      Assert.False(stayer.IsCompleted);
      pipe.Held[0].SetResult(Replies.Golden("snapshot.reply.json"));

      Assert.Equal((ServiceOutcome.Canceled, true), (left.Outcome, left.Unconfirmed));
      Assert.True((await stayer).Succeeded);
      Assert.Single(pipe.Sent);
      Assert.Equal("DEBUG Management request snapshot on the computer is no longer waited for; the service may still carry it out.", Assert.Single(log.Entries));
   }



   /// <summary>A shared read takes one place under the ceiling however many callers share it, and gives it back when it is over.</summary>
   [Fact]
   public async Task SharedRead_TakesOnePlace_HoweverManyShareIt()
   {
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());

      Task<ServiceResult<ServiceSnapshot>>[] sharing = [.. Enumerable.Range(0, 20).Select(_ => client.SnapshotAsync())];
      Assert.Single(pipe.Sent);
      for(int index = 0; index < 11; index++)
      {
         _ = client.AcknowledgeJobAsync(Server, "job-" + index);
      }
      ServiceResult<TransportResult> overTheCeiling = await AtOnce(client.TransportAsync());
      pipe.Held[0].SetResult(Replies.Golden("snapshot.reply.json"));
      ServiceResult<ServiceSnapshot>[] answers = await Task.WhenAll(sharing);
      Task<ServiceResult<TransportResult>> underIt = client.TransportAsync();

      Assert.Equal(ServiceOutcome.Skipped, overTheCeiling.Outcome);
      Assert.All(answers, answer => Assert.Same(answers[0], answer));
      Assert.Equal(13, pipe.Sent.Count);
      Assert.Equal(ManagementOperations.TransportGet, pipe.Sent[12].Op);
      Assert.False(underIt.IsCompleted);
   }



   /// <summary>Requires a call to have ended before it returned, which is what "not sent" and "answered at once" mean here, and returns the call. A call that would wait fails the test here instead of hanging it.</summary>
   private static Task<TResult> AtOnce<TResult>(Task<TResult> call)
   {
      Assert.True(call.IsCompleted, "The call should have ended without waiting for anything.");
      return call;
   }
}
