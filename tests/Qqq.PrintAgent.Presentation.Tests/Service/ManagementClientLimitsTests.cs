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
/// transport read at a time; and the snapshot a caller can ask for that is sent after its call. Each test decides
/// the order of events itself: the pipe holds every request until the test answers it, a request that is sent at
/// once is sent before the call returns, and a request that waited its turn is awaited by its arrival at the pipe.
/// Nothing here sleeps, and no time passes that a test depends on.
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
   /// only when the pipe answers or fails it: until then a read is skipped and a change waits its turn, and the
   /// change leaves when the pipe fails the request nobody waits for. The place is given back on whatever thread
   /// the pipe fails that request on, so the test waits for the change to arrive at the pipe.
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
      Task<ServiceResult<ReconnectResult>> waitsItsTurn = client.ReconnectAsync(Server);
      Assert.Equal(12, pipe.Sent.Count);
      pipe.Held[0].SetException(new IOException("The pipe closed."));
      await pipe.Arrived(13);

      Assert.Equal((ServiceOutcome.Canceled, true), (left.Outcome, left.Unconfirmed));
      Assert.Equal(ServiceOutcome.Skipped, whileHeld.Outcome);
      Assert.Equal(13, pipe.Sent.Count);
      Assert.Equal(ManagementOperations.ConnectionReconnect, pipe.Sent[12].Op);
      Assert.False(waitsItsTurn.IsCompleted);
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

      Task<ServiceResult<ServiceSnapshot>> leaver = client.SnapshotAsync(cancellationToken: leaving.Token);
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



   /// <summary>
   /// A snapshot asked for after a change was answered must show that change, so it cannot be the one that was
   /// already in flight when the change was made. Asked for as "sent after the call", it waits out the snapshot in
   /// flight and is then a second request, sent after the change's answer; a call that arrives while it waits shares
   /// that second request and not the first, whichever of the two goes on first when the first snapshot lands. The
   /// timer's call, made at the same moment, still shares the snapshot in flight.
   /// </summary>
   [Fact]
   public async Task SnapshotSentAfterTheCall_WaitsOutTheOneInFlight_AndIsARequestSentAfterTheCall()
   {
      RecordingLog log = new();
      FakePipe pipe = new();
      TaskCompletionSource<ManagementReplyParse> tick = pipe.Hold();
      pipe.Answer(Replies.Golden("servers.rename.reply.json"));
      ManagementClient client = new(pipe.SendAsync, log);

      Task<ServiceResult<ServiceSnapshot>> timer = client.SnapshotAsync();
      ServiceResult<ServerSummary> renamed = await client.RenameServerAsync(Server, "Warehouse east");
      Task<ServiceResult<ServiceSnapshot>> afterTheChange = client.SnapshotAsync(sentAfterTheCall: true);
      Task<ServiceResult<ServiceSnapshot>> alsoAfterIt = client.SnapshotAsync(sentAfterTheCall: true);
      Task<ServiceResult<ServiceSnapshot>> nextTick = client.SnapshotAsync();
      Assert.True(renamed.Succeeded);
      Assert.False(afterTheChange.IsCompleted || alsoAfterIt.IsCompleted || nextTick.IsCompleted);
      Assert.Equal([ManagementOperations.Snapshot, ManagementOperations.ServersRename], pipe.Sent.Select(request => request.Op), StringComparer.Ordinal);

      TaskCompletionSource<ManagementReplyParse> second = pipe.Hold();
      tick.SetResult(Replies.Golden("snapshot.reply.json"));
      await pipe.Arrived(3);
      ServiceResult<ServiceSnapshot> fromBeforeTheChange = await timer;
      Assert.Same(fromBeforeTheChange, await nextTick);
      Assert.False(afterTheChange.IsCompleted || alsoAfterIt.IsCompleted);
      Assert.Equal([ManagementOperations.Snapshot, ManagementOperations.ServersRename, ManagementOperations.Snapshot], pipe.Sent.Select(request => request.Op), StringComparer.Ordinal);

      second.SetResult(Replies.Golden("snapshot.healthy.reply.json"));
      ServiceResult<ServiceSnapshot> fromAfterTheChange = await afterTheChange.WaitAsync(s_guard);

      Assert.True(fromAfterTheChange.Succeeded);
      Assert.NotSame(fromBeforeTheChange, fromAfterTheChange);
      Assert.Same(fromAfterTheChange, await alsoAfterIt.WaitAsync(s_guard));
      Assert.Equal(3, pipe.Sent.Count);
      Assert.Empty(log.Entries);
   }



   /// <summary>
   /// With no snapshot in flight, a snapshot asked for as "sent after the call" is sent at once, and it is the flight
   /// the timer's calls share while it is in the air. A second such call made while it is in the air is not served
   /// by it, although it too was asked for as "sent after the call": it was sent before the second call. That call
   /// waits it out and is another request. So is a call made after an answer has landed: an answer serves only
   /// those who asked before it was sent.
   /// </summary>
   [Fact]
   public async Task SnapshotSentAfterTheCall_WithNoneInFlight_IsSentAtOnce_AndServesNoCallMadeAfterItWasSent()
   {
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());

      Task<ServiceResult<ServiceSnapshot>> asked = client.SnapshotAsync(sentAfterTheCall: true);
      Assert.Single(pipe.Sent);
      Task<ServiceResult<ServiceSnapshot>> tick = client.SnapshotAsync();
      Task<ServiceResult<ServiceSnapshot>> askedWhileItWasInFlight = client.SnapshotAsync(sentAfterTheCall: true);
      Assert.Single(pipe.Sent);
      pipe.Held[0].SetResult(Replies.Golden("snapshot.reply.json"));
      ServiceResult<ServiceSnapshot> answer = await asked;
      await pipe.Arrived(2);
      Assert.False(askedWhileItWasInFlight.IsCompleted);
      pipe.Held[1].SetResult(Replies.Golden("snapshot.healthy.reply.json"));
      ServiceResult<ServiceSnapshot> next = await askedWhileItWasInFlight.WaitAsync(s_guard);
      Task<ServiceResult<ServiceSnapshot>> askedAfterItLanded = client.SnapshotAsync(sentAfterTheCall: true);

      Assert.True(answer.Succeeded);
      Assert.Same(answer, await tick);
      Assert.NotSame(answer, next);
      Assert.Equal(3, pipe.Sent.Count);
      Assert.False(askedAfterItLanded.IsCompleted);
   }



   /// <summary>
   /// At the ceiling a snapshot asked for as "sent after the call" waits its turn and is not skipped: its caller needs
   /// the answer to decide something, and "skipped" would be none. The timer's call is still skipped meanwhile, as it
   /// always is at the ceiling. When a request in flight is done the snapshot is sent and answered.
   /// </summary>
   [Fact]
   public async Task SnapshotSentAfterTheCall_AtTheCeiling_WaitsItsTurn_AndIsNotSkipped()
   {
      RecordingLog log = new();
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient client = new(pipe.SendAsync, log);
      for(int index = 0; index < 12; index++)
      {
         _ = client.AcknowledgeJobAsync(Server, "job-" + index);
      }

      Task<ServiceResult<ServiceSnapshot>> asked = client.SnapshotAsync(sentAfterTheCall: true);
      ServiceResult<ServiceSnapshot> tick = await AtOnce(client.SnapshotAsync());
      Assert.False(asked.IsCompleted);
      Assert.Equal(12, pipe.Sent.Count);
      pipe.Held[0].SetResult(Replies.Success(new NoResult()));
      await pipe.Arrived(13);
      pipe.Held[12].SetResult(Replies.Golden("snapshot.reply.json"));
      ServiceResult<ServiceSnapshot> answer = await asked.WaitAsync(s_guard);

      Assert.Equal(ServiceOutcome.Skipped, tick.Outcome);
      Assert.Equal((ServiceOutcome.Success, false), (answer.Outcome, answer.Unconfirmed));
      Assert.Equal(ManagementOperations.Snapshot, pipe.Sent[12].Op);
      Assert.Equal(
      [
         "DEBUG Management request snapshot on the computer waits its turn: 12 requests are already in flight.",
         "DEBUG Management request snapshot on the computer was skipped: 12 requests are already in flight. It is asked again on its next tick.",
      ], log.Entries);
   }



   /// <summary>
   /// A snapshot asked for as "sent after the call" has sent nothing while it waits, and a caller that leaves then
   /// ends it with nothing sent and nothing to check: while it waits its turn at the ceiling, and while it waits out
   /// the snapshot in flight. The turn it would have had goes to the next in line, and no second snapshot follows
   /// the one that was in flight.
   /// </summary>
   [Fact]
   public async Task SnapshotSentAfterTheCall_WhoseCallerLeavesWhileItWaits_SendsNothing()
   {
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());
      using CancellationTokenSource leavingTheQueue = new();
      using CancellationTokenSource leavingTheFlight = new();
      Task<ServiceResult<ServiceSnapshot>> inFlight = client.SnapshotAsync();
      Task<ServiceResult<ServiceSnapshot>> waitingOutTheFlight = client.SnapshotAsync(sentAfterTheCall: true, leavingTheFlight.Token);
      for(int index = 1; index < 12; index++)
      {
         _ = client.AcknowledgeJobAsync(Server, "job-" + index);
      }
      await leavingTheFlight.CancelAsync();
      ServiceResult<ServiceSnapshot> leftTheFlight = await waitingOutTheFlight.WaitAsync(s_guard);
      pipe.Held[0].SetResult(Replies.Golden("snapshot.reply.json"));
      Assert.True((await inFlight).Succeeded);
      _ = client.AcknowledgeJobAsync(Server, "job-12");
      Assert.Equal(13, pipe.Sent.Count);

      Task<ServiceResult<ServiceSnapshot>> waitingItsTurn = client.SnapshotAsync(sentAfterTheCall: true, leavingTheQueue.Token);
      Task<ServiceResult<ReconnectResult>> next = client.ReconnectAsync(Server);
      await leavingTheQueue.CancelAsync();
      ServiceResult<ServiceSnapshot> leftTheQueue = await waitingItsTurn.WaitAsync(s_guard);
      pipe.Held[1].SetResult(Replies.Success(new NoResult()));
      await pipe.Arrived(14);

      Assert.Equal((ServiceOutcome.Canceled, false), (leftTheFlight.Outcome, leftTheFlight.Unconfirmed));
      Assert.Equal((ServiceOutcome.Canceled, false), (leftTheQueue.Outcome, leftTheQueue.Unconfirmed));
      Assert.Equal(ManagementOperations.ConnectionReconnect, pipe.Sent[13].Op);
      Assert.Equal(14, pipe.Sent.Count);
      Assert.Single(pipe.Sent, request => string.Equals(request.Op, ManagementOperations.Snapshot, StringComparison.Ordinal));
      Assert.False(next.IsCompleted);
   }



   /// <summary>
   /// A snapshot asked for as "sent after the call" whose caller leaves at the moment the one in flight lands has
   /// sent nothing, and its result says so, even when another caller has by then sent the next snapshot: that one was
   /// sent for the other caller. Here the cancellation itself lands the flight (a token runs its callbacks newest
   /// first, so the callback registered here runs before the waiting call is told), and the caller that stays asked
   /// first, so it goes on first and has sent the next snapshot when the one that leaves goes on. The caller that
   /// stays waits with a token of its own, which is never cancelled: a wait that can be cancelled is told of the
   /// landing in the order the waits began, on the thread that lands the flight. If a runtime orders these events
   /// otherwise, the caller that leaves is simply told before the next snapshot exists, and the test still holds.
   /// </summary>
   [Fact]
   public async Task SnapshotSentAfterTheCall_WhoseCallerLeavesAsTheOneInFlightLands_HasSentNothing()
   {
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());
      using CancellationTokenSource leaving = new();
      using CancellationTokenSource staying = new();
      _ = client.SnapshotAsync();
      Task<ServiceResult<ServiceSnapshot>> stays = client.SnapshotAsync(sentAfterTheCall: true, staying.Token);
      Task<ServiceResult<ServiceSnapshot>> leaves = client.SnapshotAsync(sentAfterTheCall: true, leaving.Token);
      using CancellationTokenRegistration lands = leaving.Token.Register(() => pipe.Held[0].SetResult(Replies.Golden("snapshot.reply.json")));

      await leaving.CancelAsync();
      ServiceResult<ServiceSnapshot> left = await leaves.WaitAsync(s_guard);
      await pipe.Arrived(2);

      Assert.Equal((ServiceOutcome.Canceled, false), (left.Outcome, left.Unconfirmed));
      Assert.Equal(2, pipe.Sent.Count);
      Assert.False(stays.IsCompleted);
   }



   /// <summary>
   /// Two snapshots asked for as "sent after the call" that both wait their turn at the ceiling cost one request and
   /// one place. The first to get a turn sends the snapshot. The second, when its turn comes, finds a snapshot that
   /// was sent after it asked, shares it, and gives its place back, so the place is not lost: once the snapshot is
   /// answered, ten requests are in flight and two more leave at once.
   /// </summary>
   [Fact]
   public async Task SnapshotSentAfterTheCall_ThatFindsOneToShareWhenItsTurnComes_GivesItsPlaceBack()
   {
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());
      Task<ServiceResult<NoResult>>[] inFlight = [.. Enumerable.Range(0, 12).Select(index => client.AcknowledgeJobAsync(Server, "job-" + index))];

      Task<ServiceResult<ServiceSnapshot>> first = client.SnapshotAsync(sentAfterTheCall: true);
      Task<ServiceResult<ServiceSnapshot>> second = client.SnapshotAsync(sentAfterTheCall: true);
      pipe.Held[0].SetResult(Replies.Success(new NoResult()));
      await pipe.Arrived(13);
      pipe.Held[1].SetResult(Replies.Success(new NoResult()));
      pipe.Held[12].SetResult(Replies.Golden("snapshot.reply.json"));
      ServiceResult<ServiceSnapshot> answer = await first.WaitAsync(s_guard);
      ServiceResult<ServiceSnapshot> shared = await second.WaitAsync(s_guard);
      Assert.True((await inFlight[0]).Succeeded && (await inFlight[1]).Succeeded);
      _ = client.ReconnectAsync(Server);
      _ = client.GetTrustAsync(Server);

      Assert.True(answer.Succeeded);
      Assert.Same(answer, shared);
      Assert.Equal(ManagementOperations.Snapshot, pipe.Sent[12].Op);
      Assert.Equal([ManagementOperations.ConnectionReconnect, ManagementOperations.TrustGet], pipe.Sent.Skip(13).Select(request => request.Op), StringComparer.Ordinal);
   }



   /// <summary>
   /// A snapshot asked for as "sent after the call" is answered by one that was sent after it asked, also when that
   /// one has landed by the time the caller's turn comes: it shares that answer and sends nothing. Here two such
   /// calls wait their turn with a change between them. The first gets a turn and sends the snapshot; when that
   /// lands, the place it gives back goes to the change, so the second still waits, and its turn comes only when
   /// another request is done. By then the snapshot it may share has landed. The pipe is given an answer for one
   /// more request, so a second snapshot would be answered and seen, and none is sent.
   /// </summary>
   [Fact]
   public async Task SnapshotSentAfterTheCall_WhoseTurnComesAfterALaterOneHasLanded_SharesItsAnswer_AndSendsNothing()
   {
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());
      for(int index = 0; index < 12; index++)
      {
         _ = client.AcknowledgeJobAsync(Server, "job-" + index);
      }

      Task<ServiceResult<ServiceSnapshot>> first = client.SnapshotAsync(sentAfterTheCall: true);
      Task<ServiceResult<ReconnectResult>> change = client.ReconnectAsync(Server);
      Task<ServiceResult<ServiceSnapshot>> second = client.SnapshotAsync(sentAfterTheCall: true);
      pipe.Held[0].SetResult(Replies.Success(new NoResult()));
      await pipe.Arrived(13);
      pipe.Held[12].SetResult(Replies.Golden("snapshot.reply.json"));
      ServiceResult<ServiceSnapshot> answer = await first.WaitAsync(s_guard);
      await pipe.Arrived(14);
      Assert.False(second.IsCompleted);
      pipe.Answer(Replies.Golden("snapshot.healthy.reply.json"));
      pipe.Held[1].SetResult(Replies.Success(new NoResult()));
      ServiceResult<ServiceSnapshot> shared = await second.WaitAsync(s_guard);

      Assert.True(answer.Succeeded);
      Assert.Same(answer, shared);
      Assert.Equal([ManagementOperations.Snapshot, ManagementOperations.ConnectionReconnect], pipe.Sent.Skip(12).Select(request => request.Op), StringComparer.Ordinal);
      Assert.False(change.IsCompleted);
   }



   /// <summary>
   /// A snapshot's result says which flight made it: the number the request was handed to the pipe with, counted from
   /// one. Everyone who shares a flight is handed the one result and so reads the same number, a timer's call and a
   /// call that asked for a snapshot sent after it alike. A flight that is refused, and one that fails in the pipe,
   /// made its result too and is numbered. The numbers rise in the order the requests were handed to the pipe, and
   /// the client's own count is the number of the last.
   /// </summary>
   [Fact]
   public async Task SnapshotResult_SaysWhichFlightMadeIt_SharersReadOneNumber_AndTheNumbersRise()
   {
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());
      Assert.Equal(0, client.SnapshotsSent);

      Task<ServiceResult<ServiceSnapshot>> first = client.SnapshotAsync();
      Task<ServiceResult<ServiceSnapshot>> sharesTheFirst = client.SnapshotAsync();
      Task<ServiceResult<ServiceSnapshot>> askedForALaterOne = client.SnapshotAsync(sentAfterTheCall: true);
      Assert.Equal(1, client.SnapshotsSent);
      pipe.Held[0].SetResult(Replies.Golden("snapshot.reply.json"));
      await pipe.Arrived(2);
      Assert.Equal(2, client.SnapshotsSent);
      Task<ServiceResult<ServiceSnapshot>> sharesTheSecond = client.SnapshotAsync();
      pipe.Held[1].SetResult(Replies.Failure(ManagementErrorCode.InternalError));
      ServiceResult<ServiceSnapshot> second = await askedForALaterOne.WaitAsync(s_guard);
      pipe.Fail(new IOException("The pipe is broken."));
      ServiceResult<ServiceSnapshot> third = await client.SnapshotAsync();

      Assert.Equal(1L, (await first).Flight);
      Assert.Same(await first, await sharesTheFirst);
      Assert.Equal(2L, second.Flight);
      Assert.Same(second, await sharesTheSecond);
      Assert.Equal(3L, third.Flight);
      Assert.Equal((ServiceOutcome.Success, ServiceOutcome.Rejected, ServiceOutcome.Unavailable), ((await first).Outcome, second.Outcome, third.Outcome));
      Assert.Equal(3, client.SnapshotsSent);
      Assert.Equal(3, pipe.Sent.Count);
   }



   /// <summary>
   /// A result that no flight made names none: a read whose caller had left before anything was sent, the result of
   /// a caller that stopped waiting for the flight it shared (the caller that stayed is handed the flight's own
   /// result, with its number), and a read that was skipped at the ceiling. The transport reads are numbered apart
   /// from the snapshots: the first of them is flight one whatever number the snapshots have reached, and it is
   /// not counted among the snapshots that were sent.
   /// </summary>
   [Fact]
   public async Task ResultNoFlightMade_NamesNone_AndTransportReadsAreNumberedApart()
   {
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());
      using CancellationTokenSource gone = new();
      using CancellationTokenSource leaving = new();
      await gone.CancelAsync();

      ServiceResult<ServiceSnapshot> notStarted = await client.SnapshotAsync(cancellationToken: gone.Token);
      Task<ServiceResult<ServiceSnapshot>> leaver = client.SnapshotAsync(cancellationToken: leaving.Token);
      Task<ServiceResult<ServiceSnapshot>> stayer = client.SnapshotAsync();
      await leaving.CancelAsync();
      ServiceResult<ServiceSnapshot> left = await leaver.WaitAsync(s_guard);
      pipe.Held[0].SetResult(Replies.Golden("snapshot.reply.json"));
      ServiceResult<ServiceSnapshot> stayed = await stayer;
      pipe.Answer(Replies.Golden("snapshot.reply.json"));
      ServiceResult<ServiceSnapshot> next = await client.SnapshotAsync();
      pipe.Answer(Replies.Golden("transport.get.reply.json"));
      ServiceResult<TransportResult> meter = await client.TransportAsync();
      Task<ServiceResult<NoResult>>[] inFlight = [.. Enumerable.Range(0, 12).Select(index => client.AcknowledgeJobAsync(Server, "job-" + index))];
      ServiceResult<ServiceSnapshot> skipped = await AtOnce(client.SnapshotAsync());

      Assert.Equal((ServiceOutcome.Canceled, false), (notStarted.Outcome, notStarted.Unconfirmed));
      Assert.Equal((ServiceOutcome.Canceled, true), (left.Outcome, left.Unconfirmed));
      Assert.Equal(ServiceOutcome.Skipped, skipped.Outcome);
      Assert.All(new[] { notStarted, left, skipped }, result => Assert.Null(result.Flight));
      Assert.All(new[] { notStarted, left, skipped }, result => Assert.Null(result.SnapshotsSent));
      Assert.Equal(1L, stayed.Flight);
      Assert.Equal(2L, next.Flight);
      Assert.True(meter.Succeeded);
      Assert.Equal(1L, meter.Flight);
      Assert.Equal(2L, meter.SnapshotsSent);
      Assert.Equal(2, client.SnapshotsSent);
      Assert.All(inFlight, request => Assert.False(request.IsCompleted));
   }



   /// <summary>
   /// The result of a change says how many snapshots the client had handed to the pipe when the change's reply was
   /// read, so a caller can tell a snapshot that may have been sent before the change was carried out from one that
   /// was sent after its answer. The count is read when the reply has arrived and not when the change was sent: a
   /// snapshot handed to the pipe while the change was in flight is counted, because the service may have answered
   /// it first. A change whose outcome is not known, because the pipe failed or because its caller stopped waiting,
   /// carries the count as it was at that moment. A request that was never handed to the pipe says nothing, and no
   /// change's result names a flight.
   /// </summary>
   [Fact]
   public async Task ChangeResult_SaysHowManySnapshotsHadBeenSent_WhenItsReplyWasRead()
   {
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());
      using CancellationTokenSource gone = new();
      using CancellationTokenSource leaving = new();
      await gone.CancelAsync();

      pipe.Answer(Replies.Golden("servers.rename.reply.json"));
      ServiceResult<ServerSummary> beforeAnySnapshot = await client.RenameServerAsync(Server, "Warehouse east");
      Task<ServiceResult<ServerSummary>> sentBeforeTheSnapshot = client.RenameServerAsync(Server, "Warehouse east");
      Task<ServiceResult<ServiceSnapshot>> inTheAir = client.SnapshotAsync();
      pipe.Held[0].SetResult(Replies.Golden("servers.rename.reply.json"));
      ServiceResult<ServerSummary> answeredWhileItFlew = await sentBeforeTheSnapshot;
      pipe.Fail(new IOException("The pipe is broken."));
      ServiceResult<ServerRemoveResult> failed = await client.RemoveServerAsync(Server);
      Task<ServiceResult<ReconnectResult>> leaver = client.ReconnectAsync(Server, leaving.Token);
      pipe.Held[1].SetResult(Replies.Golden("snapshot.reply.json"));
      ServiceResult<ServiceSnapshot> first = await inTheAir;
      pipe.Answer(Replies.Golden("snapshot.reply.json"));
      ServiceResult<ServiceSnapshot> second = await client.SnapshotAsync();
      await leaving.CancelAsync();
      ServiceResult<ReconnectResult> left = await leaver.WaitAsync(s_guard);
      ServiceResult<ReconnectResult> notStarted = await client.ReconnectAsync(Server, gone.Token);
      ServiceResult<ServerSummary> refusedHere = await client.RenameServerAsync("not a server", "Warehouse");

      Assert.True(beforeAnySnapshot.Succeeded && answeredWhileItFlew.Succeeded);
      Assert.Equal(0L, beforeAnySnapshot.SnapshotsSent);
      Assert.Equal(1L, answeredWhileItFlew.SnapshotsSent);
      Assert.Equal((ServiceOutcome.Unavailable, true), (failed.Outcome, failed.Unconfirmed));
      Assert.Equal(1L, failed.SnapshotsSent);
      Assert.Equal((ServiceOutcome.Canceled, true), (left.Outcome, left.Unconfirmed));
      Assert.Equal(2L, left.SnapshotsSent);
      Assert.Equal((1L, 2L), (first.Flight!.Value, second.Flight!.Value));
      Assert.Equal((1L, 2L), (first.SnapshotsSent!.Value, second.SnapshotsSent!.Value));
      Assert.Equal((ServiceOutcome.Canceled, false), (notStarted.Outcome, notStarted.Unconfirmed));
      Assert.Equal((ServiceOutcome.Rejected, ManagementErrorCode.InvalidRequest), (refusedHere.Outcome, refusedHere.Code));
      Assert.Null(notStarted.SnapshotsSent);
      Assert.Null(refusedHere.SnapshotsSent);
      Assert.Null(beforeAnySnapshot.Flight ?? answeredWhileItFlew.Flight ?? failed.Flight ?? left.Flight ?? notStarted.Flight ?? refusedHere.Flight);
   }



   /// <summary>
   /// A change whose turn comes at the very moment its caller leaves is not sent either. The place is handed over
   /// first, so the wait ends with a place taken and a token that is cancelled; the client then gives the place back
   /// and reports that nothing was sent. Here the cancellation itself releases the request whose place is handed
   /// over: a token runs its callbacks newest first, so the callback registered here runs before the waiter's own,
   /// and the turn is handed over while the token is already cancelled and before the waiter is told. The place
   /// that was given back is free: the next request leaves, with twelve in flight again. If a runtime orders these
   /// events otherwise, the waiter is simply told of the cancellation first, and the test still holds.
   /// </summary>
   [Fact]
   public async Task ChangeWhoseTurnComesAsItsCallerLeaves_IsNotSent_AndItsPlaceIsGivenBack()
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
      using CancellationTokenRegistration turnComes = leaving.Token.Register(() => pipe.Held[0].SetResult(Replies.Success(new NoResult())));
      await leaving.CancelAsync();
      ServiceResult<ServerRemoveResult> result = await left.WaitAsync(s_guard);
      _ = client.ReconnectAsync(Server);
      await pipe.Arrived(13);

      Assert.Equal((ServiceOutcome.Canceled, false), (result.Outcome, result.Unconfirmed));
      Assert.DoesNotContain(pipe.Sent, request => string.Equals(request.Op, ManagementOperations.ServersRemove, StringComparison.Ordinal));
      Assert.Equal(13, pipe.Sent.Count);
      Assert.Equal(ManagementOperations.ConnectionReconnect, pipe.Sent[12].Op);
      Assert.Contains($"DEBUG Management request servers.remove on {Server} was not sent: its wait had ended before it was.", log.Entries, StringComparer.Ordinal);
   }



   /// <summary>
   /// A request that waited its turn goes on outside the call that gave it the place, never inside it: the place is
   /// handed over under the ceiling's lock, and if the waiter went on there, its whole send would run while the lock
   /// is held, on the thread of the request that was just answered. So the waiting request is not handed to the pipe
   /// on the thread that delivers the answer which freed its place, while that answer is still being delivered. The
   /// answer is delivered on a thread of the pool, where the client goes on at once with the request that was
   /// answered and so gives its place back before the delivery returns.
   /// </summary>
   [Fact]
   public async Task RequestThatWaitedItsTurn_GoesOnOutsideTheCallThatGaveItItsPlace()
   {
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());
      for(int index = 0; index < 12; index++)
      {
         _ = client.AcknowledgeJobAsync(Server, "job-" + index);
      }
      int deliveringOnThread = 0;
      int sentWhileDelivering = 0;
      Task<ServiceResult<ReconnectResult>> waiting = client.ReconnectAsync(Server);
      pipe.Then(() =>
      {
         if(Volatile.Read(ref deliveringOnThread) == Environment.CurrentManagedThreadId)
         {
            Interlocked.Increment(ref sentWhileDelivering);
         }
         return Task.FromResult(Replies.Success(new ReconnectResult(ReconnectResult.Requested)));
      });

      await Task.Run(() =>
      {
         Volatile.Write(ref deliveringOnThread, Environment.CurrentManagedThreadId);
         pipe.Held[0].SetResult(Replies.Success(new NoResult()));
         Volatile.Write(ref deliveringOnThread, 0);
      });
      ServiceResult<ReconnectResult> result = await waiting.WaitAsync(s_guard);

      Assert.True(result.Succeeded);
      Assert.Equal(13, pipe.Sent.Count);
      Assert.Equal(0, Volatile.Read(ref sentWhileDelivering));
   }



   /// <summary>
   /// A wait for a turn that begins with its token already cancelled is over at once, as a cancelled task, and holds
   /// no place: the place that is given back afterwards is free, and it is the only one. That is why the client asks
   /// whether such a wait completed successfully, and not whether it completed, before it goes on as the holder of a
   /// place. No test brings the client itself there: it looks at the token just before it waits, so only a
   /// cancellation between that look and the wait would, and a test cannot place one there.
   /// </summary>
   [Fact]
   public void WaitForATurnThatBeginsCancelled_IsOverAtOnce_AndHoldsNoPlace()
   {
      RequestCeiling ceiling = new(1);
      Assert.True(ceiling.TryEnter());

      Task wait = ceiling.EnterAsync(new CancellationToken(canceled: true));
      ceiling.Leave();

      Assert.Equal((true, true, false), (wait.IsCompleted, wait.IsCanceled, wait.IsCompletedSuccessfully));
      Assert.True(ceiling.TryEnter());
      Assert.False(ceiling.TryEnter());
   }



   /// <summary>Requires a call to have ended before it returned, which is what "not sent" and "answered at once" mean here, and returns the call. A call that would wait fails the test here instead of hanging it.</summary>
   private static Task<TResult> AtOnce<TResult>(Task<TResult> call)
   {
      Assert.True(call.IsCompleted, "The call should have ended without waiting for anything.");
      return call;
   }
}
