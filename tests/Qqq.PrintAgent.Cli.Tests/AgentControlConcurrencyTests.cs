/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

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



   /// <summary>True when the agent has reported the desired-state revision as durably applied.</summary>
   private static bool Applied(RealtimeRuntimeFixture fixture, long revision)
      => fixture.Messages.Any(message => message.Type is "STATE_APPLIED" && message.Payload.GetProperty("lastAppliedRevision").GetInt64() == revision);
}
