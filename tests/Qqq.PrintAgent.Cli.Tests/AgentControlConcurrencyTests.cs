/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Remote control and printing run side by side: a server disconnect or reconnect instruction must be applied while a print is still in progress, without interrupting that print.</summary>
public sealed class AgentControlConcurrencyTests
{
   /// <summary>
   /// With a print held inside the spooler, the server marks the device disconnected. The instruction is applied and
   /// acknowledged on the realtime channel, which stays up. A second job, for the other printer, is then announced, and a
   /// download slot is free for it. Over the next two heartbeat intervals the transport ping still goes out, but no heartbeat
   /// does (no ordinary traffic while disconnected), no claim is made and the second job stays on the server, while the held
   /// job stays SUBMITTING and is submitted only once. Reconnecting restores normal state without touching the held print;
   /// when that print is released it completes as SPOOLED and the second job is claimed and printed after it. While the print
   /// is held its claim wave is still open and has already found the queue empty, so here the wave would keep the second job
   /// waiting whatever the desired state; the test after this one shows the disconnect doing so by itself.
   /// </summary>
   [Fact]
   public async Task RemoteDisconnectAndReconnectApplyDuringABlockedPrintWithoutInterruptingIt()
   {
      var clock = new ManualTimeProvider();
      await using RealtimeRuntimeFixture fixture = new(downloads: 2, timeProvider: clock);
      fixture.Printer.BlockPrint = true;
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      fixture.AddJob("job");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(fixture.Printer.Entered.Task);
      // The second download slot is free, and the lane that took it has found the queue empty.
      await fixture.WaitAsync(() => fixture.LastClaimWasEmpty);
      await connection.SendAsync("DESIRED_STATE", new DeviceDesiredState(2, true, "DISCONNECTED"));
      await fixture.WaitAsync(() => Applied(fixture, 2) && fixture.Runtime.GetTransportSnapshot().State is "CONNECTED");
      Assert.False(fixture.Runtime.GetSnapshot().Connected);
      int claims = fixture.Claims;
      await AnnounceAndPassTwoSilentHeartbeatRoundsAsync(fixture, connection, clock, "second", "other");
      Assert.Equal(claims, fixture.Claims);
      Assert.Null(fixture.State("second"));
      Assert.Equal("SUBMITTING", fixture.State("job"));
      await connection.SendAsync("DESIRED_STATE", new DeviceDesiredState(3, false, "CONNECTED"));
      await fixture.WaitAsync(() => Applied(fixture, 3) && fixture.Runtime.GetSnapshot().Connected);
      Assert.Equal("SUBMITTING", fixture.State("job"));
      Assert.Equal(1, fixture.Printer.Submissions);
      fixture.Printer.Release.TrySetResult();
      await fixture.WaitAsync(() => fixture.State("job") is "SPOOLED" && fixture.State("second") is "SPOOLED");
      Assert.Equal(2, fixture.Printer.Submissions);
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>
   /// Nothing is in progress and the device is disconnected by the server when a job is announced. The announcement starts a
   /// claim wave at once, so only the disconnect keeps the job from being claimed: after two heartbeat intervals no claim has
   /// been made, the job is still on the server and no heartbeat was sent. Reconnecting claims and prints it. A declined wave
   /// leaves no trace to wait for, so the two intervals are the time a wrong claim is given to show; a runtime that honors the
   /// disconnect can never fail here.
   /// </summary>
   [Fact]
   public async Task JobAnnouncedWhileDisconnectedIsClaimedOnlyAfterReconnect()
   {
      var clock = new ManualTimeProvider();
      await using RealtimeRuntimeFixture fixture = new(timeProvider: clock);
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      fixture.AddJob("first");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      // The claim after the printed job found the queue empty: the wave that printed it claims nothing more.
      await fixture.WaitAsync(() => fixture.State("first") is "SPOOLED" && fixture.LastClaimWasEmpty);
      await connection.SendAsync("DESIRED_STATE", new DeviceDesiredState(2, true, "DISCONNECTED"));
      await fixture.WaitAsync(() => Applied(fixture, 2) && fixture.Runtime.GetTransportSnapshot().State is "CONNECTED");
      int claims = fixture.Claims;
      await AnnounceAndPassTwoSilentHeartbeatRoundsAsync(fixture, connection, clock, "waiting", "printer");
      Assert.Equal(claims, fixture.Claims);
      Assert.Null(fixture.State("waiting"));
      Assert.Equal(1, fixture.Printer.Submissions);
      await connection.SendAsync("DESIRED_STATE", new DeviceDesiredState(3, false, "CONNECTED"));
      await fixture.WaitAsync(() => fixture.State("waiting") is "SPOOLED");
      Assert.True(fixture.Claims > claims);
      Assert.Equal(2, fixture.Printer.Submissions);
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>
   /// A new desired state arrives while the command worker is saving a command's result. The save is held open, the frame is
   /// delivered and applied in memory, and only then is the save let through. Neither may be lost. The desired state in force
   /// afterwards is the new one, in memory and on disk, and it is the only revision the agent reports as applied: reporting the
   /// old one would let a paused device print until the server noticed, and reporting the new one while holding the old would
   /// leave the connection silent. The command's result is kept beside it. The server either repeats an unapplied state, as a
   /// real one does, or stays silent; the outcome must not depend on being repaired.
   /// </summary>
   [Theory]
   [InlineData(false, false)]
   [InlineData(false, true)]
   [InlineData(true, false)]
   [InlineData(true, true)]
   public async Task StateFrameArrivingWhileACommandResultIsSavedLosesNeither(bool resumeArrives, bool serverResends)
   {
      using var saving = new ManualResetEventSlim();
      using var release = new ManualResetEventSlim();
      await using RealtimeRuntimeFixture fixture = new();
      fixture.ResendUnappliedState = serverResends;
      fixture.ControlResultSaving = () =>
      {
         saving.Set();
         release.Wait();
      };
      try
      {
         DeviceDesiredState arriving = new(2, !resumeArrives, "CONNECTED");
         RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync(desiredState: new DeviceDesiredState(1, resumeArrives, "CONNECTED"));
         await connection.SendAsync("COMMAND", RealtimeRuntimeFixture.Command("REFRESH_INVENTORY"));
         await fixture.WaitAsync(() => saving.IsSet);
         int reportedBefore = fixture.Count("STATE_APPLIED");
         await connection.SendAsync("DESIRED_STATE", arriving);
         await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Control!.DesiredState == arriving);
         release.Set();
         await fixture.WaitAsync(() => fixture.Count("STATE_APPLIED") > reportedBefore && fixture.Runtime.GetTransportSnapshot().State is "CONNECTED"
            && fixture.Messages.Any(message => message.Type is "COMMAND_RECEIPT"));
         Assert.All(fixture.Messages.Where(message => message.Type is "STATE_APPLIED").Skip(reportedBefore), message => Assert.Equal(2, message.Payload.GetProperty("lastAppliedRevision").GetInt64()));
         AgentControlState live = fixture.Runtime.GetSnapshot().Control!;
         Assert.Equal(arriving, live.DesiredState);
         Assert.Equal("REFRESH_INVENTORY", live.LastCommand);
         Assert.Equal("INVENTORY_REFRESHED", live.LastOutcome);
         using(AgentStore store = new(fixture.DirectoryPath))
         {
            AgentControlState saved = store.LoadControlState();
            Assert.Equal(arriving, saved.DesiredState);
            Assert.Equal("REFRESH_INVENTORY", saved.LastCommand);
         }
         fixture.AddJob("after");
         await connection.SendAsync("JOB_AVAILABLE", new { });
         if(resumeArrives)
         {
            await fixture.WaitAsync(() => fixture.State("after") is "SPOOLED");
         }
         else
         {
            await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Jobs.Any(job => job.JobId is "after" && job.Status is "QUEUED"));
            Assert.Null(fixture.State("after"));
            Assert.Equal(0, fixture.Printer.Prints);
         }
         Assert.Equal(0, fixture.ForbiddenPolls);
      }
      finally { release.Set(); }
   }



   /// <summary>
   /// Queues a job on the server, announces it to a disconnected device and lets two heartbeat intervals pass, asserting that
   /// the transport ping went out in both and that no heartbeat did. Frames are handled in arrival order, so the receipt of
   /// the expired command sent after the announcement proves the announcement was handled before the intervals begin.
   /// </summary>
   private static async Task AnnounceAndPassTwoSilentHeartbeatRoundsAsync(RealtimeRuntimeFixture fixture, RealtimeRuntimeFixture.Connection connection, ManualTimeProvider clock, string jobId, string printerId)
   {
      int heartbeats = fixture.Count("HEARTBEAT");
      int pings = fixture.Count("PING");
      int receipts = fixture.Count("COMMAND_RECEIPT");
      fixture.AddJob(jobId, printerId: printerId);
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await connection.SendAsync("COMMAND", RealtimeRuntimeFixture.Command(id: "marker-" + jobId, expired: true));
      await fixture.WaitAsync(() => fixture.Count("COMMAND_RECEIPT") == receipts + 1);
      await fixture.HeartbeatAsync(clock);
      await fixture.HeartbeatAsync(clock);
      // A heartbeat would follow its round's ping on the same socket, so once the second ping is here the first round sent none.
      await fixture.WaitAsync(() => fixture.Count("PING") >= pings + 2);
      Assert.Equal(heartbeats, fixture.Count("HEARTBEAT"));
   }



   /// <summary>True when the agent has reported the desired-state revision as durably applied.</summary>
   private static bool Applied(RealtimeRuntimeFixture fixture, long revision)
      => fixture.Messages.Any(message => message.Type is "STATE_APPLIED" && message.Payload.GetProperty("lastAppliedRevision").GetInt64() == revision);
}
