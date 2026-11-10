/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The download throttle reads the runtime's clock. On a manual clock its pacing is exact: each chunk waits precisely as
/// long as its bytes take at the configured rate on one shared schedule, and no test has to let real time pass to see it.
/// </summary>
public sealed class DownloadThrottleTests
{
   private const int BytesPerSecond = 256_000;
   private static readonly TimeSpan s_limit = TimeSpan.FromSeconds(30);



   /// <summary>Starts a runtime on a manual clock, connected to the scripted server with the given download rate in force.</summary>
   private static async Task<(RealtimeRuntimeFixture Fixture, ManualTimeProvider Clock)> ConnectedAsync(long bytesPerSecond)
   {
      ManualTimeProvider clock = new();
      var settings = new RealtimeSettings(Revision: 1, ReconnectMinDelayMillis: 100, ReconnectMaxDelayMillis: 1000, ReconnectJitterPercent: 0, MaxDownloadBytesPerSecond: bytesPerSecond);
      RealtimeRuntimeFixture fixture = new(settings: settings, timeProvider: clock);
      _ = await fixture.ConnectAsync();
      return (fixture, clock);
   }



   /// <summary>
   /// At 256,000 bytes a second a chunk of 32,000 bytes takes exactly 125 milliseconds. Two chunks asked for together
   /// share one schedule: the first is due after 125 milliseconds and the second after 250. Neither finishes one tick
   /// early, each finishes at its instant, and the clock holds a wait for exactly each of those moments. (A wait is a
   /// whole number of milliseconds, as every delay of the platform is, so the test uses sizes that come out whole.)
   /// </summary>
   [Fact]
   public async Task ChunksAreReleasedAtExactlyTheConfiguredRate_OnOneSharedSchedule()
   {
      (RealtimeRuntimeFixture fixture, ManualTimeProvider clock) = await ConnectedAsync(BytesPerSecond);
      await using RealtimeRuntimeFixture running = fixture;
      var perChunk = TimeSpan.FromMilliseconds(125);

      Task first = fixture.Runtime.ThrottleDownloadAsync(32_000, CancellationToken.None);
      bool firstArmed = clock.HasTimerDueIn(perChunk);
      Task second = fixture.Runtime.ThrottleDownloadAsync(32_000, CancellationToken.None);
      bool secondArmed = clock.HasTimerDueIn(perChunk + perChunk);
      clock.Advance(perChunk - TimeSpan.FromTicks(1));
      bool firstEarly = first.IsCompleted;
      clock.Advance(TimeSpan.FromTicks(1));
      await first.WaitAsync(s_limit);
      clock.Advance(perChunk - TimeSpan.FromTicks(1));
      bool secondEarly = second.IsCompleted;
      clock.Advance(TimeSpan.FromTicks(1));
      await second.WaitAsync(s_limit);

      Assert.True(firstArmed);
      Assert.True(secondArmed);
      Assert.False(firstEarly);
      Assert.False(secondEarly);
   }



   /// <summary>
   /// The schedule does not save up idle time. After the clock has moved well past the last chunk's due moment, the next
   /// chunk is paced from now: a chunk of 64,000 bytes waits exactly 250 milliseconds, not less.
   /// </summary>
   [Fact]
   public async Task AfterAnIdlePeriod_TheNextChunkIsPacedFromNow()
   {
      (RealtimeRuntimeFixture fixture, ManualTimeProvider clock) = await ConnectedAsync(BytesPerSecond);
      await using RealtimeRuntimeFixture running = fixture;
      Task warmUp = fixture.Runtime.ThrottleDownloadAsync(32_000, CancellationToken.None);
      clock.Advance(TimeSpan.FromSeconds(5));
      await warmUp.WaitAsync(s_limit);

      Task next = fixture.Runtime.ThrottleDownloadAsync(64_000, CancellationToken.None);
      bool armed = clock.HasTimerDueIn(TimeSpan.FromMilliseconds(250));
      clock.Advance(TimeSpan.FromMilliseconds(250) - TimeSpan.FromTicks(1));
      bool early = next.IsCompleted;
      clock.Advance(TimeSpan.FromTicks(1));
      await next.WaitAsync(s_limit);

      Assert.True(armed);
      Assert.False(early);
   }



   /// <summary>
   /// The pacing is exact whatever the clock reads when a chunk arrives. The schedule is kept in fractions of a second,
   /// and a wait worked out from it must not come out a hair short and lose a whole millisecond to the platform's
   /// delay, which counts in whole milliseconds. Eighty chunks arrive at irregular moments, each after the schedule has
   /// caught up; every one waits exactly 125 milliseconds.
   /// </summary>
   [Fact]
   public async Task PacingIsExact_WhateverTheClockReads()
   {
      (RealtimeRuntimeFixture fixture, ManualTimeProvider clock) = await ConnectedAsync(BytesPerSecond);
      await using RealtimeRuntimeFixture running = fixture;
      var perChunk = TimeSpan.FromMilliseconds(125);
      List<int> inexact = [];

      for(int chunk = 1; chunk <= 80; chunk++)
      {
         clock.Advance(TimeSpan.FromTicks(chunk * 7919L));
         Task waiting = fixture.Runtime.ThrottleDownloadAsync(32_000, CancellationToken.None);
         if(!clock.HasTimerDueIn(perChunk))
         {
            inexact.Add(chunk);
         }
         clock.Advance(perChunk);
         await waiting.WaitAsync(s_limit);
      }

      Assert.Empty(inexact);
   }



   /// <summary>Without a rate limit a chunk is not held at all: the throttle answers at once and arms no wait on the clock.</summary>
   [Fact]
   public async Task WithoutARateLimit_NothingWaits()
   {
      (RealtimeRuntimeFixture fixture, ManualTimeProvider clock) = await ConnectedAsync(bytesPerSecond: 0);
      await using RealtimeRuntimeFixture running = fixture;
      int armedBefore = clock.PendingTimers;

      Task unlimited = fixture.Runtime.ThrottleDownloadAsync(64 * 1024 * 1024, CancellationToken.None);

      Assert.True(unlimited.IsCompletedSuccessfully);
      Assert.Equal(armedBefore, clock.PendingTimers);
   }



   /// <summary>A chunk that is waiting for its moment gives up when its download is cancelled, without the clock moving.</summary>
   [Fact]
   public async Task CancelledDownload_StopsWaiting()
   {
      (RealtimeRuntimeFixture fixture, _) = await ConnectedAsync(BytesPerSecond);
      await using RealtimeRuntimeFixture running = fixture;
      using CancellationTokenSource cancellation = new();

      Task waiting = fixture.Runtime.ThrottleDownloadAsync(32_000, cancellation.Token);
      bool pending = !waiting.IsCompleted;
      await cancellation.CancelAsync();

      Assert.True(pending);
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(s_limit));
   }
}
