/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Shell;

/// <summary>
/// The two reads the window repeats. A test ticks the shell by calling them and decides itself when each read is
/// answered, by holding the request in the scripted pipe; nothing here waits for time to pass.
/// </summary>
public sealed class ShellPollingTests
{
   /// <summary>The operations of the requests sent so far.</summary>
   private static string Operations(ShellFixture fixture) => string.Join(' ', fixture.Pipe.Sent.Select(request => request.Op));



   /// <summary>A transport result for the servers with these numbers.</summary>
   private static TransportResult Transport(params int[] numbers)
      => new([.. numbers.Select(number => new ServerTransport(Snapshots.Id(number), new AgentTransportSnapshot("WSS", "CONNECTED", number, 2, 3, 4, DateTimeOffset.UnixEpoch), DateTimeOffset.UnixEpoch))]);



   /// <summary>
   /// A tick that arrives while the read it would start is still in flight starts nothing and is handed the read
   /// that is on its way, so neither read ever overlaps itself; the two reads are independent, so a snapshot that is
   /// slow does not stop the meter. When a read has ended the next tick starts a new one.
   /// </summary>
   [Fact]
   public async Task Polling_Snapshot_Transport_NeverOverlap()
   {
      using ShellFixture fixture = new();
      fixture.Pipe.HoldEverything();

      Task first = fixture.Shell.RefreshAsync();
      Task second = fixture.Shell.RefreshAsync();
      Task third = fixture.Shell.RefreshAsync();

      Assert.Same(first, second);
      Assert.Same(first, third);
      Assert.False(first.IsCompleted);
      Assert.Equal("snapshot", Operations(fixture));

      Task meter = fixture.Shell.RefreshTransportAsync();
      Assert.Same(meter, fixture.Shell.RefreshTransportAsync());
      Assert.Equal("snapshot transport.get", Operations(fixture));

      fixture.Pipe.Held[1].SetResult(Replies.Success(Transport(1)));
      await meter;
      Assert.False(first.IsCompleted);
      Assert.NotNull(fixture.Shell.TransportOf(Snapshots.Id(1)));
      Task nextMeter = fixture.Shell.RefreshTransportAsync();
      Assert.NotSame(meter, nextMeter);
      Assert.Equal("snapshot transport.get transport.get", Operations(fixture));

      fixture.Pipe.Held[0].SetResult(Replies.Success(Snapshots.WithServers(1)));
      await first;
      Assert.Single(fixture.Shell.Tree.Servers);
      Task next = fixture.Shell.RefreshAsync();
      Assert.NotSame(first, next);
      Assert.Equal("snapshot transport.get transport.get snapshot", Operations(fixture));
      Assert.All(fixture.Pipe.Sent, request => Assert.Null(request.Server));
   }



   /// <summary>The reads never enter the gate: while a command holds a server, the computer and the wizard, both reads are sent and shown, for the busy server too.</summary>
   [Fact]
   public async Task Polling_GoesOnWhileAServerIsBusy()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2));
      Assert.True(fixture.Shell.TryBeginCommand(Snapshots.Id(1), "Signing in to Server 1.", out IDisposable? server));
      Assert.True(fixture.Shell.TryBeginCommand(OperationGate.Computer, "Saving the computer's details.", out IDisposable? computer));
      Assert.True(fixture.Gate.TryEnter(OperationGate.Wizard, "Checking the server.", out IDisposable? wizard, out _));

      await fixture.Tick(Snapshots.Server(1, health: ServerSnapshot.Red), Snapshots.Server(2, health: ServerSnapshot.Orange, transport: "CONNECTING"));
      fixture.Pipe.Answer(Replies.Success(Transport(1, 2)));
      await fixture.Shell.RefreshTransportAsync();

      Assert.Equal("snapshot snapshot transport.get", Operations(fixture));
      Assert.Equal([HealthLevel.Red, HealthLevel.Orange], fixture.Shell.Tree.Servers.Select(node => node.Health.Level));
      Assert.Equal([true, false], fixture.Shell.Tree.Servers.Select(node => node.IsBusy));
      Assert.Equal(1, fixture.Shell.TransportOf(Snapshots.Id(1))!.Transport.UploadedBytes);
      Assert.Equal("2 servers: 1 needs attention, 1 not ready", fixture.Shell.Footer.Text);
      server.Dispose();
      computer.Dispose();
      wizard.Dispose();
   }



   /// <summary>
   /// A snapshot says what was true when it was asked for. What it says about a server that was changed from this
   /// window after that is not shown, because it is older than the change; what it says about the other servers is.
   /// The next snapshot, asked for after the change, is shown for every server.
   /// </summary>
   [Fact]
   public async Task Snapshot_OlderThanALocalChange_SkipsThatServer_AppliesTheOthers()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "Warehouse"), Snapshots.Server(2, "Bench"));
      TaskCompletionSource<ManagementReplyParse> held = fixture.Pipe.Hold();
      Task refresh = fixture.Shell.RefreshAsync();

      Assert.True(fixture.Shell.TryBeginCommand(Snapshots.Id(1), "Renaming Warehouse.", out IDisposable? rename));
      rename.Dispose();
      held.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(1, "Stale name", health: ServerSnapshot.Red), Snapshots.Server(2, "Bench east", health: ServerSnapshot.Red))));
      await refresh;

      Assert.Equal("Warehouse,Bench east", fixture.ServerNames);
      Assert.Equal([HealthLevel.Green, HealthLevel.Red], fixture.Shell.Tree.Servers.Select(node => node.Health.Level));
      Assert.Equal("Warehouse", fixture.Shell.Snapshot!.Servers[0].Name);
      Assert.Equal("2 servers: 1 needs attention, 1 ready", fixture.Shell.Footer.Text);

      await fixture.Tick(Snapshots.Server(1, "Warehouse east", health: ServerSnapshot.Red), Snapshots.Server(2, "Bench east", health: ServerSnapshot.Red));

      Assert.Equal("Warehouse east,Bench east", fixture.ServerNames);
      Assert.Equal([HealthLevel.Red, HealthLevel.Red], fixture.Shell.Tree.Servers.Select(node => node.Health.Level));
   }



   /// <summary>
   /// What counts is whether the server changed after the snapshot was asked for: a command that had ended before
   /// that does not hold the snapshot back; one that starts after it does, while it runs and after it has ended; and
   /// a change that was made without the gate and noted counts as one.
   /// </summary>
   [Fact]
   public async Task Snapshot_AndALocalChange_OnlyAChangeAfterTheRequestHoldsItBack()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "One"), Snapshots.Server(2, "Two"));

      Assert.True(fixture.Shell.TryBeginCommand(Snapshots.Id(1), "Working.", out IDisposable? ended));
      ended.Dispose();
      await fixture.Tick(Snapshots.Server(1, "One b"), Snapshots.Server(2, "Two"));
      Assert.Equal("One b,Two", fixture.ServerNames);

      Assert.True(fixture.Shell.TryBeginCommand(Snapshots.Id(1), "Working.", out IDisposable? running));
      await fixture.Tick(Snapshots.Server(1, "One c"), Snapshots.Server(2, "Two"));
      Assert.Equal("One c,Two", fixture.ServerNames);

      TaskCompletionSource<ManagementReplyParse> held = fixture.Pipe.Hold();
      Task refresh = fixture.Shell.RefreshAsync();
      running.Dispose();
      held.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(1, "One d"), Snapshots.Server(2, "Two d"))));
      await refresh;
      Assert.Equal("One c,Two d", fixture.ServerNames);

      held = fixture.Pipe.Hold();
      refresh = fixture.Shell.RefreshAsync();
      Assert.True(fixture.Shell.TryBeginCommand(Snapshots.Id(2), "Working.", out IDisposable? started));
      held.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(1, "One e"), Snapshots.Server(2, "Two e"))));
      await refresh;
      Assert.Equal("One e,Two d", fixture.ServerNames);
      started.Dispose();

      held = fixture.Pipe.Hold();
      refresh = fixture.Shell.RefreshAsync();
      fixture.Shell.Busy.NoteChange(Snapshots.Id(1));
      held.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(1, "One f"), Snapshots.Server(2, "Two f"))));
      await refresh;
      Assert.Equal("One e,Two f", fixture.ServerNames);
   }



   /// <summary>
   /// A snapshot that is older than a local change keeps a changed server that it no longer lists, at its place, and
   /// lists a new server it has that the window did not. A change to the computer (its details, the order of the
   /// servers) holds back the snapshot's computer part and its order, and the servers' own entries are still shown.
   /// </summary>
   [Fact]
   public async Task Snapshot_OlderThanALocalChange_KeepsWhatChanged_TakesTheRest()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "One"), Snapshots.Server(2, "Two"), Snapshots.Server(3, "Three"));

      TaskCompletionSource<ManagementReplyParse> held = fixture.Pipe.Hold();
      Task refresh = fixture.Shell.RefreshAsync();
      fixture.Shell.Busy.NoteChange(Snapshots.Id(2));
      held.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(1, "One b"), Snapshots.Server(3, "Three b"), Snapshots.Server(4, "Four"))));
      await refresh;
      Assert.Equal("One b,Two,Three b,Four", fixture.ServerNames);

      held = fixture.Pipe.Hold();
      refresh = fixture.Shell.RefreshAsync();
      fixture.Shell.Busy.NoteChange(OperationGate.Computer);
      held.SetResult(Replies.Success(new ServiceSnapshot(Snapshots.Computer(update: new UpdateState(true)),
         [Snapshots.Server(5, "Five"), Snapshots.Server(4, "Four c"), Snapshots.Server(3, "Three c"), Snapshots.Server(1, "One c")])));
      await refresh;

      Assert.Equal("One c,Three c,Four c,Five", fixture.ServerNames);
      Assert.False(fixture.Shell.Snapshot!.Computer.Update.Paused);
      Assert.Equal(ServiceAvailabilityKind.Reached, fixture.Shell.Availability.Kind);

      await fixture.Tick(new ServiceSnapshot(Snapshots.Computer(update: new UpdateState(true)), [Snapshots.Server(5, "Five"), Snapshots.Server(1, "One c")]));

      Assert.Equal("Five,One c", fixture.ServerNames);
      Assert.True(fixture.Shell.Snapshot!.Computer.Update.Paused);
      Assert.Equal(ServiceAvailabilityKind.UpdatePaused, fixture.Shell.Availability.Kind);
   }



   /// <summary>
   /// A page whose command has ended asks for a snapshot that is newer than its change. A read that was already in
   /// flight was sent before the change: it is waited for, what it says about the changed server is not shown, and
   /// then a new read is made, whose answer is shown for every server. Nothing is sent a second time while the
   /// earlier read is still in flight.
   /// </summary>
   [Fact]
   public async Task RefreshAfterChange_WaitsForTheReadInFlight_ThenReadsAgain()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "Warehouse"), Snapshots.Server(2, "Bench"));
      TaskCompletionSource<ManagementReplyParse> sentEarlier = fixture.Pipe.Hold();
      TaskCompletionSource<ManagementReplyParse> sentAfter = fixture.Pipe.Hold();
      Task tick = fixture.Shell.RefreshAsync();
      Assert.True(fixture.Shell.TryBeginCommand(Snapshots.Id(1), "Renaming Warehouse.", out IDisposable? rename));
      rename.Dispose();

      Task afterChange = fixture.Shell.RefreshAfterChangeAsync();

      Assert.False(afterChange.IsCompleted);
      Assert.Equal("snapshot snapshot", Operations(fixture));
      sentEarlier.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(1, "Warehouse"), Snapshots.Server(2, "Bench east"))));
      await tick;
      await fixture.Pipe.Arrived(3);
      Assert.Equal("Warehouse,Bench east", fixture.ServerNames);
      Assert.False(afterChange.IsCompleted);
      Assert.Equal("snapshot snapshot snapshot", Operations(fixture));

      sentAfter.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(1, "Warehouse east"), Snapshots.Server(2, "Bench east"))));
      await afterChange;

      Assert.Equal("Warehouse east,Bench east", fixture.ServerNames);
      Assert.Equal("snapshot snapshot snapshot", Operations(fixture));
   }



   /// <summary>
   /// Several pages ask for a snapshot after a change while one read is in flight. They share the one read that
   /// follows it, which is sent after each of their calls, so one further request is sent and not one for each of
   /// them. A page that asks once that read has been sent needs a later one, and gets its own. The shell is used
   /// on one thread here, as a window uses it, so the order of what happens is the order the window would see.
   /// </summary>
   [Fact]
   public void RefreshAfterChange_SeveralWaitersBehindOneRead_ShareTheOneThatFollows()
   {
      using PumpContext context = new();
      context.Run(async () =>
      {
         using ShellFixture fixture = new();
         await fixture.Tick(Snapshots.Server(1, "Warehouse"));
         TaskCompletionSource<ManagementReplyParse> sentEarlier = fixture.Pipe.Hold();
         TaskCompletionSource<ManagementReplyParse> sentAfter = fixture.Pipe.Hold();
         TaskCompletionSource<ManagementReplyParse> sentLast = fixture.Pipe.Hold();
         Task tick = fixture.Shell.RefreshAsync();

         Task first = fixture.Shell.RefreshAfterChangeAsync();
         Task second = fixture.Shell.RefreshAfterChangeAsync();
         Task third = fixture.Shell.RefreshAfterChangeAsync();
         sentEarlier.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(1, "Warehouse"))));
         await tick;

         Assert.Equal("snapshot snapshot snapshot", Operations(fixture));
         Assert.False(first.IsCompleted || second.IsCompleted || third.IsCompleted);

         Task later = fixture.Shell.RefreshAfterChangeAsync();
         sentAfter.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(1, "Warehouse east"))));
         await first;

         Assert.True(second.IsCompleted && third.IsCompleted);
         Assert.Equal("Warehouse east", fixture.ServerNames);
         await fixture.Pipe.Arrived(4);
         Assert.Equal("snapshot snapshot snapshot snapshot", Operations(fixture));
         Assert.False(later.IsCompleted);

         sentLast.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(1, "Warehouse north"))));
         await later;

         Assert.Equal("Warehouse north", fixture.ServerNames);
         Assert.Equal("snapshot snapshot snapshot snapshot", Operations(fixture));
      });
   }



   /// <summary>A page that asks for a snapshot after a change from inside the shell's own telling that a snapshot was shown is not told "nothing": the read being shown is waited for, and a new one follows it.</summary>
   [Fact]
   public void RefreshAfterChange_AskedForWhileASnapshotIsBeingShown_ReadsAgainAfterIt()
   {
      using PumpContext context = new();
      context.Run(async () =>
      {
         using ShellFixture fixture = new();
         TaskCompletionSource<ManagementReplyParse> shown = fixture.Pipe.Hold();
         fixture.Pipe.Answer(Replies.Success(Snapshots.Of(Snapshots.Server(1, "Warehouse east"))));
         List<Task> asked = [];
         fixture.Shell.SnapshotApplied += (_, _) =>
         {
            if(asked.Count == 0)
            {
               asked.Add(fixture.Shell.RefreshAfterChangeAsync());
            }
         };
         Task tick = fixture.Shell.RefreshAsync();

         shown.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(1, "Warehouse"))));
         await tick;
         Assert.False(Assert.Single(asked).IsCompleted);
         await asked[0];

         Assert.Equal("snapshot snapshot", Operations(fixture));
         Assert.Equal("Warehouse east", fixture.ServerNames);
      });
   }



   /// <summary>
   /// A page is told, with each snapshot that is shown, which servers' entries were held back because the server
   /// was changed from this window after the snapshot was asked for, and whether the computer's part and the order
   /// were. The entry the window keeps for such a server is the one from before the change, the same object. A
   /// snapshot that was asked for after the change holds nothing back, and neither does a read that brought none.
   /// </summary>
   [Fact]
   public void Snapshot_Applied_SaysWhichServersWereHeldBack()
   {
      using PumpContext context = new();
      context.Run(async () =>
      {
         using ShellFixture fixture = new();
         List<string> heldBack = [];
         fixture.Shell.SnapshotApplied += (_, applied) => heldBack.Add(string.Join(',', applied.HeldBack));
         await fixture.Tick(Snapshots.Server(1, "Warehouse"), Snapshots.Server(2, "Bench"));
         ServerSnapshot before = fixture.Shell.Snapshot!.Servers[0];
         TaskCompletionSource<ManagementReplyParse> held = fixture.Pipe.Hold();
         Task tick = fixture.Shell.RefreshAsync();
         Assert.True(fixture.Shell.TryBeginCommand(Snapshots.Id(1), "Renaming Warehouse.", out IDisposable? rename));
         rename.Dispose();
         Assert.True(fixture.Shell.TryBeginCommand(OperationGate.Computer, "Moving Warehouse down.", out IDisposable? reorder));
         reorder.Dispose();

         held.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(2, "Bench east"), Snapshots.Server(1, "Stale name"))));
         await tick;

         Assert.Equal(["", Snapshots.Id(1) + "," + OperationGate.Computer], heldBack);
         Assert.Same(before, fixture.Shell.Snapshot!.Servers[0]);
         Assert.Equal("Warehouse,Bench east", fixture.ServerNames);

         held = fixture.Pipe.Hold();
         tick = fixture.Shell.RefreshAsync();
         Assert.True(fixture.Shell.TryBeginCommand(Snapshots.Id(2), "Renaming Bench.", out rename));
         rename.Dispose();
         held.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(1, "Warehouse east"), Snapshots.Server(2, "Stale name"))));
         await tick;
         await fixture.Tick(Snapshots.Server(1, "Warehouse east"), Snapshots.Server(2, "Bench north"));
         await fixture.TickFailing(new IOException("The pipe is broken."));

         Assert.Equal(["", Snapshots.Id(1) + "," + OperationGate.Computer, Snapshots.Id(2), "", ""], heldBack);
         Assert.Equal("Warehouse east,Bench north", fixture.ServerNames);
         Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("another thread", StringComparison.Ordinal));
      });
   }



   /// <summary>With no read in flight the snapshot after a change is asked for at once; while it is in flight a tick starts nothing and is handed it; a shell that was disposed reads nothing, and neither does a call made while a snapshot is being shown that was answered before the tick's own call returned, as only a scripted pipe answers.</summary>
   [Fact]
   public async Task RefreshAfterChange_WithNoReadInFlight_ReadsAtOnce()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "Warehouse"));
      TaskCompletionSource<ManagementReplyParse> held = fixture.Pipe.Hold();

      Task afterChange = fixture.Shell.RefreshAfterChangeAsync();
      Task tick = fixture.Shell.RefreshAsync();

      Assert.Equal("snapshot snapshot", Operations(fixture));
      held.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(1, "Warehouse east"))));
      await afterChange;
      await tick;
      Assert.Equal("Warehouse east", fixture.ServerNames);
      Assert.Equal("snapshot snapshot", Operations(fixture));

      List<Task> asked = [];
      fixture.Shell.SnapshotApplied += (_, _) => asked.Add(fixture.Shell.RefreshAfterChangeAsync());
      await fixture.Tick(Snapshots.Server(1, "Warehouse east"));
      Assert.True(Assert.Single(asked).IsCompleted);
      Assert.Equal("snapshot snapshot snapshot", Operations(fixture));

      fixture.Shell.Dispose();
      await fixture.Shell.RefreshAfterChangeAsync();
      Assert.Equal("snapshot snapshot snapshot", Operations(fixture));
   }



   /// <summary>With eight servers a tick of each timer is still one request: one snapshot and one transport read for the whole computer, and none that names a server.</summary>
   [Fact]
   public async Task EightServers_OneSnapshotAndOneTransportRead_PerTick()
   {
      using ShellFixture fixture = new();

      for(int tick = 0; tick < 3; tick++)
      {
         await fixture.Tick(Snapshots.WithServers(8));
         fixture.Pipe.Answer(Replies.Success(Transport(1, 2, 3, 4, 5, 6, 7, 8)));
         await fixture.Shell.RefreshTransportAsync();
      }

      Assert.Equal(8, fixture.Shell.Tree.Servers.Count);
      Assert.Equal("snapshot transport.get snapshot transport.get snapshot transport.get", Operations(fixture));
      Assert.All(fixture.Pipe.Sent, request => Assert.Null(request.Server));
      Assert.All(Enumerable.Range(1, 8), number => Assert.Equal(number, fixture.Shell.TransportOf(Snapshots.Id(number))!.Transport.UploadedBytes));
   }



   /// <summary>A read that was skipped because the client already had as many requests in flight as it allows itself is not a failure: nothing is sent, nothing on screen changes, nothing is announced and no warning is written.</summary>
   [Fact]
   public async Task SkippedRead_IsNotAFailure_ChangesNothing()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1));
      fixture.Pipe.Answer(Replies.Success(Transport(1)));
      await fixture.Shell.RefreshTransportAsync();
      fixture.Pipe.HoldEverything();
      Task[] inFlight = [.. Enumerable.Range(0, ManagementClient.MaximumRequestsInFlight).Select(index => fixture.Client.AcknowledgeJobAsync(Snapshots.Id(1), "job-" + index))];
      List<string?> raised = [];
      int applied = 0;
      int meter = 0;
      fixture.Shell.PropertyChanged += (_, change) => raised.Add(change.PropertyName);
      fixture.Shell.SnapshotApplied += (_, _) => applied++;
      fixture.Shell.TransportChanged += (_, _) => meter++;
      int sent = fixture.Pipe.Sent.Count;
      int warnings = fixture.Log.Entries.Count(entry => !entry.StartsWith("DEBUG", StringComparison.Ordinal));

      await fixture.Shell.RefreshAsync();
      await fixture.Shell.RefreshTransportAsync();

      Assert.Equal(sent, fixture.Pipe.Sent.Count);
      Assert.Empty(raised);
      Assert.Equal((0, 0), (applied, meter));
      Assert.Equal(ServiceAvailabilityKind.Reached, fixture.Shell.Availability.Kind);
      Assert.Equal(HealthLevel.Green, fixture.Shell.Tree.Servers[0].Health.Level);
      Assert.NotNull(fixture.Shell.TransportOf(Snapshots.Id(1)));
      Assert.Single(fixture.Announced);
      Assert.Equal(warnings, fixture.Log.Entries.Count(entry => !entry.StartsWith("DEBUG", StringComparison.Ordinal)));
      Assert.All(inFlight, request => Assert.False(request.IsCompleted));
   }



   /// <summary>The meter of each server comes from the last transport read that succeeded; after one that failed or was refused there is none, so a page says the meter is unavailable and shows no old figures.</summary>
   [Fact]
   public async Task Transport_IsKeptPerServer_AndClearedByAFailedRead()
   {
      using ShellFixture fixture = new();
      int changed = 0;
      fixture.Shell.TransportChanged += (_, _) => changed++;
      Assert.Null(fixture.Shell.TransportOf(Snapshots.Id(1)));

      fixture.Pipe.Answer(Replies.Golden("transport.get.reply.json"));
      await fixture.Shell.RefreshTransportAsync();

      ServerTransport first = fixture.Shell.TransportOf("3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f")!;
      Assert.Equal(("CONNECTED", 1048576L), (first.Transport.State, first.Transport.UploadedBytes));
      Assert.NotNull(first.LastContact);
      Assert.Equal("STOPPED", fixture.Shell.TransportOf("3f2b8c1e-0000-4f6b-9a7c-0e1d2c3b4a60")!.Transport.State);
      Assert.Null(fixture.Shell.TransportOf(Snapshots.Id(1)));
      Assert.Equal(1, changed);

      fixture.Pipe.Fail(new IOException("The pipe is broken."));
      await fixture.Shell.RefreshTransportAsync();
      Assert.Null(fixture.Shell.TransportOf("3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f"));
      Assert.Equal(2, changed);

      fixture.Pipe.Answer(Replies.Success(new TransportResult([.. Transport(1).Servers, .. Transport(1).Servers])));
      await fixture.Shell.RefreshTransportAsync();
      Assert.NotNull(fixture.Shell.TransportOf(Snapshots.Id(1)));

      fixture.Pipe.Answer(Replies.Failure(ManagementErrorCode.InternalError));
      await fixture.Shell.RefreshTransportAsync();
      Assert.Null(fixture.Shell.TransportOf(Snapshots.Id(1)));
      Assert.Equal(4, changed);
   }



   /// <summary>Each snapshot that is shown is said once, after everything follows it; the log says when what is known of the service changes, and not on every tick.</summary>
   [Fact]
   public async Task Snapshot_Applied_IsRaisedOnce_AndTheLogFollowsTheServiceOnlyWhenItChanges()
   {
      using ShellFixture fixture = new();
      List<string> seen = [];
      fixture.Shell.SnapshotApplied += (sender, _) =>
      {
         Assert.Same(fixture.Shell, sender);
         seen.Add(fixture.Shell.Availability.Kind + " " + fixture.ServerNames + " " + fixture.Shell.Footer.Text);
      };

      await fixture.Tick(Snapshots.Server(1, "One"));
      await fixture.Tick(Snapshots.Server(1, "One"));
      await fixture.TickFailing(new IOException("The pipe is broken."));
      await fixture.TickFailing(new IOException("The pipe is broken."));
      await fixture.Tick(Snapshots.Server(1, "One"));

      Assert.Equal(["Reached One Connected and ready", "Reached One Connected and ready", "NotReached One Local print service unavailable", "NotReached One Local print service unavailable",
         "Reached One Connected and ready"], seen);
      Assert.Equal(["INFO The local print service answered a snapshot (Reached).", "WARN The local print service is not available to this window (NotReached).",
         "INFO The local print service answered a snapshot (Reached)."], fixture.Log.Entries.Where(entry => entry.Contains("local print service", StringComparison.Ordinal)), StringComparer.Ordinal);
      Assert.DoesNotContain(fixture.Log.Values, value => value is string text && text.Contains("One", StringComparison.Ordinal));
   }



   /// <summary>A handler that asks for a refresh while a snapshot is being shown starts no second read.</summary>
   [Fact]
   public async Task Refresh_AskedForWhileASnapshotIsBeingShown_StartsNothing()
   {
      using ShellFixture fixture = new();
      List<Task> asked = [];
      fixture.Shell.SnapshotApplied += (_, _) => asked.Add(fixture.Shell.RefreshAsync());

      await fixture.Tick(Snapshots.Server(1));
      await fixture.Tick(Snapshots.Server(1));

      Assert.Equal("snapshot snapshot", Operations(fixture));
      Assert.Equal(2, asked.Count);
      Assert.All(asked, task => Assert.True(task.IsCompleted));
   }



   /// <summary>A shell that was disposed reads nothing, shows nothing of a read that was in flight, and no longer follows the gate.</summary>
   [Fact]
   public async Task Disposed_ReadsNothing_ShowsNothing()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "One"));
      TaskCompletionSource<ManagementReplyParse> snapshot = fixture.Pipe.Hold();
      TaskCompletionSource<ManagementReplyParse> transport = fixture.Pipe.Hold();
      Task refresh = fixture.Shell.RefreshAsync();
      Task meter = fixture.Shell.RefreshTransportAsync();

      fixture.Shell.Dispose();
      snapshot.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(1, "Changed"))));
      transport.SetResult(Replies.Success(Transport(1)));
      await refresh;
      await meter;

      Assert.Equal("One", fixture.ServerNames);
      Assert.Null(fixture.Shell.TransportOf(Snapshots.Id(1)));
      int sent = fixture.Pipe.Sent.Count;
      await fixture.Shell.RefreshAsync();
      await fixture.Shell.RefreshTransportAsync();
      Assert.Equal(sent, fixture.Pipe.Sent.Count);
      Assert.True(fixture.Gate.TryEnter(Snapshots.Id(1), "Working.", out IDisposable? hold, out _));
      Assert.False(fixture.Shell.Tree.Servers[0].IsBusy);
      Assert.False(fixture.Shell.Busy.ChangedSince(Snapshots.Id(1), 0));
      hold.Dispose();
   }



   /// <summary>A read that is answered on another thread is shown on the thread the tick came from: the shell awaits the client without leaving it.</summary>
   [Fact]
   public void Refresh_AnsweredOnAnotherThread_IsShownOnTheThreadThatTicked()
   {
      using PumpContext context = new();
      context.Run(async () =>
      {
         using ShellFixture fixture = new();
         int ticked = Environment.CurrentManagedThreadId;
         List<int> shownOn = [];
         fixture.Shell.PropertyChanged += (_, _) => shownOn.Add(Environment.CurrentManagedThreadId);
         fixture.Shell.FooterAnnouncement.Raised += (_, _) => shownOn.Add(Environment.CurrentManagedThreadId);
         TaskCompletionSource<ManagementReplyParse> held = fixture.Pipe.Hold();
         Task refresh = fixture.Shell.RefreshAsync();

         await Task.Run(() => held.SetResult(Replies.Success(Snapshots.WithServers(2))));
         await refresh;

         Assert.NotEmpty(shownOn);
         Assert.All(shownOn, thread => Assert.Equal(ticked, thread));
         Assert.Equal(2, fixture.Shell.Tree.Servers.Count);
      });
   }



   /// <summary>The shell needs everything it is built from.</summary>
   [Fact]
   public void Shell_RefusesToBeBuiltFromNothing()
   {
      using ShellFixture fixture = new();
      ManagementClient client = fixture.Client;

      Assert.Equal("client", Assert.Throws<ArgumentNullException>(() => new ShellViewModel(null!, fixture.Gate, fixture.Wizard, fixture.Log, TestData.Time, "Product")).ParamName);
      Assert.Equal("gate", Assert.Throws<ArgumentNullException>(() => new ShellViewModel(client, null!, fixture.Wizard, fixture.Log, TestData.Time, "Product")).ParamName);
      Assert.Equal("wizard", Assert.Throws<ArgumentNullException>(() => new ShellViewModel(client, fixture.Gate, null!, fixture.Log, TestData.Time, "Product")).ParamName);
      Assert.Equal("log", Assert.Throws<ArgumentNullException>(() => new ShellViewModel(client, fixture.Gate, fixture.Wizard, null!, TestData.Time, "Product")).ParamName);
      Assert.Equal("time", Assert.Throws<ArgumentNullException>(() => new ShellViewModel(client, fixture.Gate, fixture.Wizard, fixture.Log, null!, "Product")).ParamName);
      Assert.Equal("productName", Assert.Throws<ArgumentNullException>(() => new ShellViewModel(client, fixture.Gate, fixture.Wizard, fixture.Log, TestData.Time, null!)).ParamName);
   }
}
