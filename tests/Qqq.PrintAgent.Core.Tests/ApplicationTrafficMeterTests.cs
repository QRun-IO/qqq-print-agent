/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Transport;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>Unit tests of the traffic meter's arithmetic, with a controllable clock: exact totals under concurrency, rates that fall to zero when idle, and counters that saturate instead of wrapping.</summary>
public sealed class ApplicationTrafficMeterTests
{
   /// <summary>
   /// A thousand concurrent uploads and downloads add up exactly with no lost updates. Rates reflect the recent window and drop
   /// to zero after two idle seconds while totals stay, and the "since" timestamp does not change when traffic resumes.
   /// </summary>
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



   /// <summary>Totals that would exceed the 64-bit maximum stay at the maximum instead of wrapping negative, rates stay finite, and a negative byte count is rejected.</summary>
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



   /// <summary>A thread-safe clock that moves only when advanced, so rate windows can be crossed without sleeping.</summary>
   private sealed class TrafficClock : TimeProvider
   {
      private long _ticks;



      /// <summary>One timestamp unit per tick, so timestamps and TimeSpan ticks share a scale.</summary>
      public override long TimestampFrequency => TimeSpan.TicksPerSecond;



      /// <summary>The current fake timestamp, read atomically.</summary>
      public override long GetTimestamp() => Interlocked.Read(ref _ticks);



      /// <summary>The fake time as a UTC instant counted from the Unix epoch.</summary>
      public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());



      /// <summary>Moves the fake clock forward.</summary>
      internal void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
   }
}
