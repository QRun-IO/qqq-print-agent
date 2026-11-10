/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Checks the claim drain's pacing against a scripted server: one wake-up drains a whole backlog back to back, and an empty or
/// failing queue ends the drain instead of being asked again and again.
/// </summary>
public sealed class AgentRuntimeBacklogTests
{
   /// <summary>
   /// Three jobs wait on the server while a claim drain is already in progress. That one drain claims and prints all three in
   /// order with no further signal from the server. It ends at the first empty claim, or, when the server fails instead, after
   /// each of its lanes has tried once. One more drain is pending from a wake-up that arrived meanwhile and ends the same way;
   /// after that no claim is made until the server announces a job, which is then drained by exactly one job claim and one empty one.
   ///
   /// The claim counts are exact, with no waiting to see whether more arrive, because every wake-up is accounted for before
   /// claims are let through. The first claim is held at the server, so one drain is in progress. The WELCOME wake-up was
   /// issued before the state frame sent here could be applied, and that frame's own wake-up before the settings frame after
   /// it; the wake-up for the first inventory was issued before the printer catalog was read. All of them fall inside the
   /// drain in progress and leave exactly one drain pending.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task Backlog_DrainsOnOneWakeUp_ThenStopsClaimingOnEmptyOrError(bool failWhenEmpty)
   {
      await using RealtimeRuntimeFixture fixture = new();
      fixture.ClaimHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      fixture.FailEmptyClaims = failWhenEmpty;
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      await fixture.WaitAsync(fixture.ClaimArrived.Task);
      await connection.SendAsync("DESIRED_STATE", new DeviceDesiredState(2, false, "CONNECTED"));
      await connection.SendAsync("SETTINGS", fixture.Settings with { Revision = 2 });
      await fixture.WaitAsync(() => fixture.Messages.Any(message => message.Type is "STATE_APPLIED" && message.Payload.GetProperty("lastSettingsRevision").GetInt64() == 2));
      await fixture.WaitAsync(() => fixture.CatalogRequests > 0);
      await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Connected);
      fixture.AddJob("job1");
      fixture.AddJob("job2");
      fixture.AddJob("job3");
      fixture.ClaimHold.SetResult();
      int endOfDrain = failWhenEmpty ? AgentRuntime.DrainLanes : 1;
      int idle = 3 + (2 * endOfDrain);
      await fixture.WaitAsync(() => fixture.Claims == idle);
      Assert.Equal(new[] { "job1", "job2", "job3" }, fixture.Printer.Order, StringComparer.Ordinal);
      Assert.All(new[] { "job1", "job2", "job3" }, job => Assert.Equal("SPOOLED", fixture.State(job)));
      fixture.FailEmptyClaims = false;
      fixture.AddJob("late");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.State("late") is "SPOOLED" && fixture.LastClaimWasEmpty);
      Assert.Equal(idle + 2, fixture.Claims);
      Assert.Equal(failWhenEmpty ? 2 * AgentRuntime.DrainLanes : 0, fixture.Logs.Count(line => line.StartsWith("Print lane stopped", StringComparison.Ordinal)));
      Assert.Equal(4, fixture.Printer.Prints);
      Assert.False(fixture.Running.IsCompleted);
      Assert.Equal(0, fixture.ForbiddenPolls);
   }
}
