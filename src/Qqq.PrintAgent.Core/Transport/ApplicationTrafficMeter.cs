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

   public ApplicationTrafficMeter(TimeProvider? timeProvider = null)
   {
      _time = timeProvider ?? TimeProvider.System;
      _started = _time.GetTimestamp();
      TotalsSinceUtc = _time.GetUtcNow();
   }

   public DateTimeOffset TotalsSinceUtc { get; }

   public void RecordUpload(long bytes) => Record(bytes, upload: true);
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

   private long CurrentBucket() => Math.Max(0, _time.GetElapsedTime(_started, _time.GetTimestamp()).Ticks / BucketTicks);

   private static long SaturatingSum(long current, long addition) => current > long.MaxValue - addition ? long.MaxValue : current + addition;

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
