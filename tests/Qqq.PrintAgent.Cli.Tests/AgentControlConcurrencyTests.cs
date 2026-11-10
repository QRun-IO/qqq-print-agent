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
   /// acknowledged on the realtime channel, which stays up: over the next two heartbeat intervals the transport ping still
   /// goes out, but no heartbeat does (no ordinary traffic while disconnected) and no further claim is made, and the held job
   /// stays SUBMITTING. Reconnecting restores normal state, and when the print is released the job completes as SPOOLED.
   /// </summary>
   [Fact]
   public async Task RemoteDisconnectAndReconnectApplyDuringABlockedPrintWithoutInterruptingIt()
   {
      var clock = new ManualTimeProvider();
      await using RealtimeRuntimeFixture fixture = new(timeProvider: clock);
      fixture.Printer.BlockPrint = true;
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      fixture.AddJob("job");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(fixture.Printer.Entered.Task);
      await connection.SendAsync("DESIRED_STATE", new DeviceDesiredState(2, true, "DISCONNECTED"));
      await fixture.WaitAsync(() => Applied(fixture, 2) && fixture.Runtime.GetTransportSnapshot().State is "CONNECTED");
      Assert.False(fixture.Runtime.GetSnapshot().Connected);
      int claims = fixture.Claims;
      int heartbeats = fixture.Count("HEARTBEAT");
      int pings = fixture.Count("PING");
      await fixture.HeartbeatAsync(clock);
      await fixture.HeartbeatAsync(clock);
      // A heartbeat would follow its round's ping on the same socket, so once the second ping is here the first round sent none.
      await fixture.WaitAsync(() => fixture.Count("PING") >= pings + 2);
      Assert.Equal(heartbeats, fixture.Count("HEARTBEAT"));
      Assert.Equal(claims, fixture.Claims);
      Assert.Equal("SUBMITTING", Assert.Single(fixture.Runtime.GetSnapshot().Jobs).Status);
      await connection.SendAsync("DESIRED_STATE", new DeviceDesiredState(3, false, "CONNECTED"));
      await fixture.WaitAsync(() => Applied(fixture, 3) && fixture.Runtime.GetSnapshot().Connected);
      Assert.Equal("SUBMITTING", fixture.State("job"));
      fixture.Printer.Release.TrySetResult();
      await fixture.WaitAsync(() => fixture.State("job") is "SPOOLED");
      Assert.Equal(1, fixture.Printer.Prints);
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



   /// <summary>True when the agent has reported the desired-state revision as durably applied.</summary>
   private static bool Applied(RealtimeRuntimeFixture fixture, long revision)
      => fixture.Messages.Any(message => message.Type is "STATE_APPLIED" && message.Payload.GetProperty("lastAppliedRevision").GetInt64() == revision);
}
