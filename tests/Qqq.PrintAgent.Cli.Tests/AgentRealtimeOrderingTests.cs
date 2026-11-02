/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentRealtimeOrderingTests
{
   [Fact]
   public async Task OlderStateAckCannotReleaseSubmissionBeforeNewSettingsAreAcknowledged()
   {
      await using var fixture = new RealtimeRuntimeFixture();
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      byte[] bytes = Encoding.ASCII.GetBytes("%PDF-synthetic-slow");
      using var slow = new GatedDocument(bytes);
      fixture.AddJob("slow", () => slow, length: bytes.Length);
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
      var observed = new TaskCompletionSource<(bool Connected, string? State, int Prints)>(TaskCreationOptions.RunContinuationsAsynchronously);
      fixture.BeforeAcknowledgement = async message =>
      {
         if(message.Type is not "STATE_APPLIED")
         { return; }
         if(message.Payload.GetProperty("lastSettingsRevision").GetInt64() == 1)
         {
            // The same server writer sends SETTINGS before ACK1: causal ordering,
            // without withholding an ACK across test-thread scheduling or sleeps.
            await connection.SendAsync("SETTINGS", fixture.Settings with { Revision = 2, MaxPdfBytes = 1 });
         }
         else
         {
            observed.TrySetResult((fixture.Runtime.GetSnapshot().Connected, fixture.State("slow"), fixture.Printer.Prints));
         }
      };
      try
      {
         await connection.SendAsync("DESIRED_STATE", new Qqq.PrintAgent.Core.Printing.DeviceDesiredState(2, false, "CONNECTED"));
         (bool connected, string? state, int prints) = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
         Assert.False(connected);
         Assert.Equal("RECEIVED", state);
         Assert.Equal(0, prints);
         slow.Release.TrySetResult();
         await fixture.WaitAsync(() => fixture.State("slow") is "FAILED");
      }
      finally { slow.Release.TrySetResult(); }
   }

   [Fact]
   public async Task StickyClaimIsNotRequestedAgainBeforeReceivedAcknowledgement()
   {
      await using var fixture = new RealtimeRuntimeFixture(downloads: 2);
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      fixture.ReceivedRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
      fixture.AddJob("first");
      fixture.AddJob("second", printerId: "other");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Messages.Any(message => message.Type is "JOB_EVENT"));
      int claims = fixture.Claims;
      await Task.Delay(150);
      Assert.Equal(claims, fixture.Claims);
      fixture.ReceivedRelease.TrySetResult();
      await fixture.WaitAsync(() => fixture.Printer.Prints == 2);
      Assert.Equal(2, fixture.Printer.Order.Distinct(StringComparer.Ordinal).Count());
      Assert.DoesNotContain(fixture.Logs, line => line.Contains("same key", StringComparison.OrdinalIgnoreCase));
   }

   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task SubmissionUsesCurrentPolicyAfterTransferAndPauseOnlyStopsNewClaims(bool rejectSize)
   {
      await using var fixture = new RealtimeRuntimeFixture();
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      byte[] bytes = Encoding.ASCII.GetBytes("%PDF-synthetic-slow");
      using var slow = new GatedDocument(bytes);
      fixture.AddJob("slow", () => slow, length: bytes.Length);
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
      await connection.SendAsync("SETTINGS", fixture.Settings with { Revision = 2, NativeSubmissionTimeoutMillis = 2345, MaxPdfBytes = rejectSize ? 1 : fixture.Settings.MaxPdfBytes });
      await connection.SendAsync("DESIRED_STATE", new Qqq.PrintAgent.Core.Printing.DeviceDesiredState(2, true, "CONNECTED"));
      await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Control!.DesiredState.DispatchPaused);
      slow.Release.TrySetResult();
      await fixture.WaitAsync(() => fixture.State("slow") is "FAILED" or "SPOOLED");
      Assert.Equal(rejectSize ? "FAILED" : "SPOOLED", fixture.State("slow"));
      Assert.Equal(rejectSize ? 0 : 1, fixture.Printer.Prints);
      if(!rejectSize)
      { Assert.Equal(TimeSpan.FromMilliseconds(2345), fixture.Printer.LastSubmissionTimeout); }
   }

   [Fact]
   public async Task IncreasingConcurrencyStartsWaitingOtherPrinterDuringAnActiveDownload()
   {
      await using var fixture = new RealtimeRuntimeFixture(downloads: 1);
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      byte[] bytes = Encoding.ASCII.GetBytes("%PDF-synthetic-slow");
      using var slow = new GatedDocument(bytes);
      fixture.AddJob("slow", () => slow, length: bytes.Length);
      fixture.AddJob("fast", printerId: "other");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
      try
      {
         Assert.Empty(fixture.Printer.Order);
         await connection.SendAsync("SETTINGS", fixture.Settings with { Revision = 2, MaxConcurrentDownloads = 2 });
         await fixture.WaitAsync(() => string.Equals(fixture.State("fast"), "SPOOLED", StringComparison.Ordinal));
         Assert.Equal("fast", Assert.Single(fixture.Printer.Order));
         slow.Release.TrySetResult();
         await fixture.WaitAsync(() => fixture.Printer.Prints == 2);
      }
      finally { slow.Release.TrySetResult(); }
   }

   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task ParallelDownloadsPreserveQueueOrderWithoutBlockingDifferentPrinters(bool samePrinter)
   {
      await using var fixture = new RealtimeRuntimeFixture(downloads: 2);
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      byte[] slowBytes = Encoding.ASCII.GetBytes("%PDF-synthetic-slow");
      byte[] fastBytes = Encoding.ASCII.GetBytes("%PDF-synthetic-fast");
      using var slow = new GatedDocument(slowBytes);
      var fastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      fixture.AddJob("slow", () => slow, length: slowBytes.Length);
      fixture.AddJob("fast", () => { fastStarted.TrySetResult(); return new MemoryStream(fastBytes); }, samePrinter ? "printer" : "other", fastBytes.Length);
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
      await fastStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
      try
      {
         if(samePrinter)
         {
            await Task.Delay(150);
            Assert.Empty(fixture.Printer.Order);
         }
         else
         {
            await fixture.WaitAsync(() => string.Equals(fixture.State("fast"), "SPOOLED", StringComparison.Ordinal));
            Assert.Equal("fast", Assert.Single(fixture.Printer.Order));
         }
         slow.Release.TrySetResult();
         await fixture.WaitAsync(() => fixture.Printer.Prints == 2);
         Assert.Equal(samePrinter ? new[] { "slow", "fast" } : ["fast", "slow"], fixture.Printer.Order, StringComparer.Ordinal);
         Assert.Equal(0, fixture.ForbiddenPolls);
      }
      finally { slow.Release.TrySetResult(); }
   }

   private sealed class GatedDocument(byte[] bytes) : MemoryStream(bytes)
   {
      internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
      {
         Entered.TrySetResult();
         await Release.Task.WaitAsync(cancellationToken);
         return await base.ReadAsync(buffer, cancellationToken);
      }
   }
}
