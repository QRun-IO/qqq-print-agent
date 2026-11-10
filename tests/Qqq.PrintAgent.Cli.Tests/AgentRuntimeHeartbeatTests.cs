/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Checks that the agent keeps telling the server it is alive while a print is stuck, so a slow spooler is not mistaken for a dead device.</summary>
public sealed class AgentRuntimeHeartbeatTests
{
   /// <summary>
   /// While a submission is held inside the printer, three heartbeat intervals pass on a manual clock and each one produces a
   /// heartbeat on the realtime channel. Afterwards the snapshot still reports connected with a recent contact, although a full
   /// heartbeat timeout has gone by on that clock since the connection was made, and the job shows SUBMITTING. Heartbeats and
   /// the contact time therefore run independently of the print in progress.
   /// </summary>
   [Fact]
   public async Task HeartbeatsContinueAndUpdateRealContactDuringBlockedSubmission()
   {
      var clock = new ManualTimeProvider();
      await using RealtimeRuntimeFixture fixture = new(timeProvider: clock);
      fixture.Printer.BlockPrint = true;
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      fixture.AddJob("job");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(fixture.Printer.Entered.Task);
      for(int round = 1; round <= 3; round++)
      {
         await fixture.HeartbeatAsync(clock);
         await fixture.WaitAsync(() => fixture.Count("HEARTBEAT") >= round);
      }
      Assert.Equal(3, fixture.Count("HEARTBEAT"));
      AgentSnapshot snapshot = fixture.Runtime.GetSnapshot();
      Assert.True(snapshot.Connected);
      Assert.NotNull(snapshot.LastSuccessfulPoll);
      Assert.Equal("SUBMITTING", Assert.Single(snapshot.Jobs).Status);
      Assert.Equal(0, fixture.Printer.Prints);
      Assert.False(fixture.Running.IsCompleted);
      Assert.Equal(0, fixture.ForbiddenPolls);
   }
}
