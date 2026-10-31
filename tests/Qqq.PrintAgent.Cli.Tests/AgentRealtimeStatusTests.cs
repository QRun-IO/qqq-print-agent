using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentRealtimeStatusTests
{
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

   private sealed class ObservationClock : TimeProvider
   {
      private long _offsetTicks;
      internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _offsetTicks, elapsed.Ticks);
      public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));
   }
}
