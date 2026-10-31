using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentRealtimeBandwidthTests
{
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

   private static void AddDocument(RealtimeRuntimeFixture fixture, string id, string printerId, byte[] bytes, GatedDocument stream)
   {
      fixture.Documents[id] = () => stream;
      fixture.Queue.Enqueue(new PrintJob(id, printerId, "Synthetic bandwidth fixture", "PDF", Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length,
         new PrintOptions(), RealtimeRuntimeFixture.Now(), "QUEUED"));
   }

   private sealed class GatedDocument(byte[] bytes, Task release) : MemoryStream(bytes)
   {
      internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

      public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
      {
         Entered.TrySetResult();
         await release.WaitAsync(cancellationToken);
         return await base.ReadAsync(buffer, cancellationToken);
      }
   }
}
