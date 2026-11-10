/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Verifies how the running service quiesces itself so an MSI update never lands in the middle of a print.</summary>
public sealed class AgentRuntimeUpdateTests
{
   private const string Quiesced = "Printing paused for an application update after active submission finished.";



   /// <summary>
   /// While a submission is held inside the printer, prepare-update applies the pause at once but answers only after that
   /// submission has finished and been recorded. Once ready, a job the server announces is shown as queued but not claimed,
   /// while a heartbeat interval passes and the heartbeat is still sent, so the server keeps seeing the machine as alive.
   /// Resuming claims and prints the waiting job.
   /// </summary>
   [Fact]
   public async Task UpdatePreparationDrainsActiveWorkThenPausesClaimsButKeepsHeartbeats()
   {
      var clock = new ManualTimeProvider();
      await using RealtimeRuntimeFixture fixture = new(timeProvider: clock);
      fixture.Printer.BlockPrint = true;
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      fixture.AddJob("active");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(fixture.Printer.Entered.Task);
      Task<string> preparation = fixture.PipeAsync(new PipeRequest("prepare-update"));
      await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Status.StartsWith("Printing paused for application update.", StringComparison.Ordinal));
      // The pause is in force and the submission is still inside the printer, so the request must still be waiting for it.
      Assert.False(preparation.IsCompleted);
      Assert.DoesNotContain(Quiesced, fixture.Logs, StringComparer.Ordinal);
      Assert.Equal("SUBMITTING", fixture.State("active"));
      fixture.Printer.Release.TrySetResult();
      Assert.Equal("Ready for update.", await preparation);
      Assert.Equal("SPOOLED", fixture.State("active"));
      Assert.Contains(Quiesced, fixture.Logs, StringComparer.Ordinal);
      int claims = fixture.Claims;
      int heartbeats = fixture.Count("HEARTBEAT");
      fixture.AddJob("waiting");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Jobs.Any(job => job.JobId is "waiting" && job.Status is "QUEUED"));
      await fixture.HeartbeatAsync(clock);
      await fixture.WaitAsync(() => fixture.Count("HEARTBEAT") > heartbeats);
      Assert.Equal(claims, fixture.Claims);
      Assert.Null(fixture.State("waiting"));
      Assert.Equal("Printing resumed.", await fixture.PipeAsync(new PipeRequest("resume-after-update")));
      await fixture.WaitAsync(() => fixture.State("waiting") is "SPOOLED");
      Assert.Equal(new[] { "active", "waiting" }, fixture.Printer.Order, StringComparer.Ordinal);
      Assert.Equal(0, fixture.ForbiddenPolls);
   }
}
