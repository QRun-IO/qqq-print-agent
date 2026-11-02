/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>What the status screen shows while the realtime channel is up: the waiting-job preview follows server signals only, and the "connected" indicator uses the negotiated heartbeat timeout.</summary>
public sealed class AgentRealtimeStatusTests
{
   /// <summary>While dispatch is paused, a job the server announces appears as QUEUED in the preview without printing, and when the server drops it the next signal removes it from the preview. Nothing prints and no HTTP polling happens, so the preview changes only when the server tells the agent.</summary>
   [Fact]
   public async Task PausedQueuePreviewRefreshesOnlyOnSignalsIncludingCancellation()
   {
      await using var fixture = new RealtimeRuntimeFixture();
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      await connection.SendAsync("DESIRED_STATE", new DeviceDesiredState(2, true, "CONNECTED"));
      await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Control!.DesiredState.DispatchPaused);
      fixture.AddJob("waiting");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Jobs.Any(job => job.JobId is "waiting" && job.Status is "QUEUED"));
      Assert.True(fixture.Queue.TryDequeue(out _));
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Jobs.All(job => job.JobId is not "waiting"));
      Assert.Equal(0, fixture.Printer.Prints);
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>With a negotiated heartbeat timeout of 120 seconds, the agent still reports connected after 45 seconds of silence and reports disconnected only after 135, so freshness follows the agreed timeout rather than a fixed 30 seconds.</summary>
   [Fact]
   public async Task ConnectionFreshnessUsesNegotiatedHeartbeatTimeoutRatherThanThirtySeconds()
   {
      var time = new ObservationClock();
      var settings = new RealtimeSettings(Revision: 1, HeartbeatIntervalSeconds: 60, HeartbeatTimeoutSeconds: 120);
      await using var fixture = new RealtimeRuntimeFixture(settings: settings, timeProvider: time);
      _ = await fixture.ConnectAsync();
      time.Advance(TimeSpan.FromSeconds(45));
      Assert.True(fixture.Runtime.GetSnapshot().Connected);
      time.Advance(TimeSpan.FromSeconds(90));
      Assert.False(fixture.Runtime.GetSnapshot().Connected);
   }



   /// <summary>A clock that runs normally but can be pushed forward, so heartbeat staleness can be reached without waiting.</summary>
   private sealed class ObservationClock : TimeProvider
   {
      private long _offsetTicks;



      /// <summary>Adds to the offset applied to the real clock.</summary>
      internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _offsetTicks, elapsed.Ticks);



      /// <summary>The real UTC time plus any advanced offset.</summary>
      public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));
   }
}
