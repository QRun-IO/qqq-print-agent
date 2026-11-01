/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Transport;

/// <summary>Application payload bytes only: no headers, framing, TLS, credentials or document contents.</summary>
public sealed class ApplicationTrafficMeter
{
   private const int BucketCount = 8;
   private const long BucketTicks = TimeSpan.TicksPerSecond / 4;
   private readonly TimeProvider _time;
   private readonly long _started;
   private readonly Lock _samplesLock = new();
   private readonly long[] _buckets = Enumerable.Repeat(-1L, BucketCount).ToArray();
   private readonly long[] _uploads = new long[BucketCount];
   private readonly long[] _downloads = new long[BucketCount];
   private long _uploaded;
   private long _downloaded;



   /// <summary>Starts the meter at the current instant. The time source is injectable so rate windows can be tested without waiting.</summary>
   public ApplicationTrafficMeter(TimeProvider? timeProvider = null)
   {
      _time = timeProvider ?? TimeProvider.System;
      _started = _time.GetTimestamp();
      TotalsSinceUtc = _time.GetUtcNow();
   }



   /// <summary>The moment the running totals began, shown with them so "total since" is honest after a service restart.</summary>
   public DateTimeOffset TotalsSinceUtc { get; }



   /// <summary>Counts payload bytes sent to the server.</summary>
   public void RecordUpload(long bytes) => Record(bytes, upload: true);



   /// <summary>Counts payload bytes received from the server.</summary>
   public void RecordDownload(long bytes) => Record(bytes, upload: false);



   /// <summary>Rates use a bounded two-second window, sampled in quarter-second buckets; idle rates become zero.</summary>
   public ApplicationTrafficSnapshot GetSnapshot()
   {
      long upload = 0;
      long download = 0;
      lock(_samplesLock)
      {
         long current = CurrentBucket();
         for(int index = 0; index < BucketCount; index++)
         {
            if(_buckets[index] >= 0 && _buckets[index] > current - BucketCount && _buckets[index] <= current)
            {
               upload = SaturatingSum(upload, _uploads[index]);
               download = SaturatingSum(download, _downloads[index]);
            }
         }
      }
      return new ApplicationTrafficSnapshot(Interlocked.Read(ref _uploaded), Interlocked.Read(ref _downloaded), upload / 2.0, download / 2.0, TotalsSinceUtc);
   }



   /// <summary>
   /// Adds bytes to the lifetime total and to the current quarter-second bucket. A bucket that last held an older interval
   /// is reset before it is reused, which is how stale traffic ages out of the rate window. Zero is ignored and a negative
   /// count is a caller error.
   /// </summary>
   private void Record(long bytes, bool upload)
   {
      ArgumentOutOfRangeException.ThrowIfNegative(bytes);
      if(bytes == 0)
      { return; }
      if(upload)
      { SaturatingAdd(ref _uploaded, bytes); }
      else
      { SaturatingAdd(ref _downloaded, bytes); }
      lock(_samplesLock)
      {
         long current = CurrentBucket();
         int index = (int)(current % BucketCount);
         if(_buckets[index] != current)
         {
            _buckets[index] = current;
            _uploads[index] = 0;
            _downloads[index] = 0;
         }
         if(upload)
         { _uploads[index] = SaturatingSum(_uploads[index], bytes); }
         else
         { _downloads[index] = SaturatingSum(_downloads[index], bytes); }
      }
   }



   /// <summary>The number of the current quarter-second interval since the meter started, from the monotonic clock so a wall-clock change cannot distort rates.</summary>
   private long CurrentBucket() => Math.Max(0, _time.GetElapsedTime(_started, _time.GetTimestamp()).Ticks / BucketTicks);



   /// <summary>Adds two non-negative counts, stopping at the maximum instead of wrapping to a negative number.</summary>
   private static long SaturatingSum(long current, long addition) => current > long.MaxValue - addition ? long.MaxValue : current + addition;



   /// <summary>Adds to a shared counter without a lock, retrying if another thread changed it first, and saturating at the maximum.</summary>
   private static void SaturatingAdd(ref long counter, long addition)
   {
      long previous;
      long next;
      do
      {
         previous = Interlocked.Read(ref counter);
         next = SaturatingSum(previous, addition);
      }
      while(Interlocked.CompareExchange(ref counter, next, previous) != previous);
   }
}
