/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Transport;

namespace Qqq.PrintAgent.Core.Tests;

public sealed class ApplicationTrafficMeterTests
{
   [Fact]
   public void ConcurrentPayloadsHaveExactTotalsAndIdleRatesReturnToZero()
   {
      var time = new TrafficClock();
      var meter = new ApplicationTrafficMeter(time);
      Assert.Equal(0, meter.GetSnapshot().DownloadBytesPerSecond);
      Parallel.For(0, 1000, _ => { meter.RecordUpload(11); meter.RecordDownload(31); });
      ApplicationTrafficSnapshot active = meter.GetSnapshot();
      Assert.Equal(11_000, active.UploadedBytes);
      Assert.Equal(31_000, active.DownloadedBytes);
      Assert.Equal(5_500, active.UploadBytesPerSecond);
      Assert.Equal(15_500, active.DownloadBytesPerSecond);
      time.Advance(TimeSpan.FromSeconds(2));
      ApplicationTrafficSnapshot idle = meter.GetSnapshot();
      Assert.Equal(0, idle.UploadBytesPerSecond);
      Assert.Equal(0, idle.DownloadBytesPerSecond);
      Assert.Equal(active.UploadedBytes, idle.UploadedBytes);
      Assert.Equal(active.DownloadedBytes, idle.DownloadedBytes);
      meter.RecordDownload(50);
      Assert.Equal(25, meter.GetSnapshot().DownloadBytesPerSecond);
      Assert.Equal(active.TotalsSinceUtc, meter.GetSnapshot().TotalsSinceUtc);
   }

   [Fact]
   public void CounterOverflowSaturatesWithoutNegativeOrInfiniteObservations()
   {
      var meter = new ApplicationTrafficMeter();
      meter.RecordUpload(long.MaxValue - 3);
      meter.RecordUpload(10);
      meter.RecordDownload(long.MaxValue);
      meter.RecordDownload(1);
      ApplicationTrafficSnapshot snapshot = meter.GetSnapshot();
      Assert.Equal(long.MaxValue, snapshot.UploadedBytes);
      Assert.Equal(long.MaxValue, snapshot.DownloadedBytes);
      Assert.True(double.IsFinite(snapshot.UploadBytesPerSecond));
      Assert.True(double.IsFinite(snapshot.DownloadBytesPerSecond));
      Assert.Throws<ArgumentOutOfRangeException>(() => meter.RecordDownload(-1));
   }

   private sealed class TrafficClock : TimeProvider
   {
      private long _ticks;
      public override long TimestampFrequency => TimeSpan.TicksPerSecond;
      public override long GetTimestamp() => Interlocked.Read(ref _ticks);
      public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
      internal void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
   }
}
