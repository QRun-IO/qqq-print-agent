/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>The server can cap how fast documents download; the cap applies to the whole agent, not to each download separately.</summary>
public sealed class AgentRealtimeBandwidthTests
{
   /// <summary>
   /// Two 256 KiB documents download at once under a 128 KiB/s aggregate cap. Both downloads are held open first, so timing starts
   /// only when both are really reading; together they need about four seconds at the shared rate, whereas two independent per-file
   /// limits would finish in about two. Finishing in under 3.5 seconds therefore means the cap was not shared. Both jobs still print.
   /// </summary>
   [Fact]
   public async Task ConcurrentDownloadsShareOneConfiguredBandwidthBudget()
   {
      const int documentBytes = 256 * 1024;
      const int aggregateBytesPerSecond = 128 * 1024;
      var settings = new RealtimeSettings(Revision: 1, MaxConcurrentPrints: 2, MaxDownloadBytesPerSecond: aggregateBytesPerSecond);
      await using var fixture = new RealtimeRuntimeFixture(downloads: 2, settings: settings);
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      byte[] bytes = new byte[documentBytes];
      Encoding.ASCII.GetBytes("%PDF-1.7 synthetic bandwidth fixture").CopyTo(bytes, 0);
      var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      using var first = new GatedDocument(bytes, release.Task);
      using var second = new GatedDocument(bytes, release.Task);
      AddDocument(fixture, "first-budget", "printer", bytes, first);
      AddDocument(fixture, "second-budget", "other", bytes, second);
      await connection.SendAsync("JOB_AVAILABLE", new { });

      try
      {
         // Both real runtime lanes must reach their independent body streams before
         // timing starts, so serialized admission cannot imitate an aggregate cap.
         await Task.WhenAll(first.Entered.Task, second.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
         long started = Stopwatch.GetTimestamp();
         release.TrySetResult();
         await fixture.WaitAsync(() => fixture.Printer.Prints == 2).WaitAsync(TimeSpan.FromSeconds(10));
         TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

         // Together these bodies need four seconds at the shared rate; independent
         // per-file throttles would permit both to finish after about two seconds.
         Assert.True(elapsed >= TimeSpan.FromSeconds(3.5), "Concurrent documents exceeded the configured aggregate download rate.");
         await fixture.WaitAsync(() => fixture.State("first-budget") is "SPOOLED" && fixture.State("second-budget") is "SPOOLED").WaitAsync(TimeSpan.FromSeconds(5));
         Assert.Equal(0, fixture.ForbiddenPolls);
      }
      finally { release.TrySetResult(); }
   }



   /// <summary>Queues a PDF job for the given printer and registers the held-open stream the fake server will serve as its document.</summary>
   private static void AddDocument(RealtimeRuntimeFixture fixture, string id, string printerId, byte[] bytes, GatedDocument stream)
   {
      fixture.Documents[id] = () => stream;
      fixture.Queue.Enqueue(new PrintJob(id, printerId, "Synthetic bandwidth fixture", "PDF", Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length,
         new PrintOptions(), RealtimeRuntimeFixture.Now(), "QUEUED"));
   }



   /// <summary>A document body that signals when reading begins and withholds its bytes until a shared release task completes, so two downloads can be started together.</summary>
   private sealed class GatedDocument(byte[] bytes, Task release) : MemoryStream(bytes)
   {
      internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);



      /// <summary>Signals the first read, waits for the shared release, then reads normally.</summary>
      public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
      {
         Entered.TrySetResult();
         await release.WaitAsync(cancellationToken);
         return await base.ReadAsync(buffer, cancellationToken);
      }
   }
}
