/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// End-to-end behavior of the realtime (WebSocket) print path in a real agent runtime: server instructions arrive promptly even
/// while a document is downloading, idle connections stay quiet, and a reconnect replays what the server never acknowledged
/// without printing anything twice. The fixture treats any HTTP polling route as a failure.
/// </summary>
public sealed class AgentRealtimeTests
{
   /// <summary>
   /// While a large document download is stuck, a pause is applied and a cancel command for that job is answered with a receipt,
   /// and the job ends FAILED without ever reaching the printer. After resuming, a small job prints normally. No HTTP polling
   /// route is touched at any point, which shows the push channel alone carries these instructions.
   /// </summary>
   [Fact]
   public async Task PauseAndCancelRemainResponsiveDuringLargeDownloadWithoutHttpPolling()
   {
      await using var fixture = new RealtimeRuntimeFixture();
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      using var stalled = new StalledDocument();
      fixture.AddJob("large", () => stalled);
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await stalled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
      await connection.SendAsync("DESIRED_STATE", new DeviceDesiredState(2, true, "CONNECTED"));
      await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Control!.DesiredState.DispatchPaused);
      Assert.Equal(0, fixture.Printer.Prints);
      string expires = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O", CultureInfo.InvariantCulture);
      await connection.SendAsync("COMMAND", new DeviceControlCommand("cancel-large", "request-large", "CANCEL_SPOOL", RealtimeRuntimeFixture.Now(), expires, "large"));
      await fixture.WaitAsync(() => fixture.Messages.Any(message => message.Type is "COMMAND_RECEIPT"
         && message.Payload.GetProperty("receipt").GetProperty("outcome").GetString() is "CANCEL_REQUESTED"));
      await fixture.WaitAsync(() => string.Equals(fixture.State("large"), "FAILED", StringComparison.Ordinal));
      Assert.Equal(0, fixture.Printer.Prints);
      await connection.SendAsync("DESIRED_STATE", new DeviceDesiredState(3, false, "CONNECTED"));
      fixture.AddJob("small");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => string.Equals(fixture.State("small"), "SPOOLED", StringComparison.Ordinal));
      Assert.Equal(1, fixture.Printer.Prints);
      await fixture.WaitAsync(() => fixture.LastClaimWasEmpty);
      // Resume and JOB_AVAILABLE both wake a finite drain. An empty response does
      // not mean the other explicit wake has finished; idle coverage is separate.
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>With nothing to do, repeated heartbeats do not add claims or control polls: only the initial drains (after the server's welcome and the first inventory) may claim, so an idle device does not hammer the server.</summary>
   [Fact]
   public async Task IdleHeartbeatsDoNotPollForJobsOrControls()
   {
      var settings = new RealtimeSettings(Revision: 1, HeartbeatIntervalSeconds: 5, HeartbeatTimeoutSeconds: 10);
      await using var fixture = new RealtimeRuntimeFixture(settings: settings, cacheSettings: true);
      _ = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => fixture.LastClaimWasEmpty);
      // WELCOME and the first inventory publication each permit one initial drain.
      // With no further server signals or inventory changes, successive real
      // heartbeat cycles must not introduce claims or HTTP control polls.
      await fixture.WaitAsync(() => fixture.Messages.Count(message => message.Type is "HEARTBEAT") >= 2);
      Assert.InRange(fixture.Claims, 1, 2);
      Assert.Equal(0, fixture.ForbiddenPolls);
      Assert.Equal(0, fixture.Printer.Prints);
      Assert.True(fixture.Runtime.GetSnapshot().Connected);
   }



   /// <summary>
   /// The server never acknowledges the SPOOLED receipt and the connection drops. After reconnecting, that same receipt (same
   /// message id) is sent again, and a job queued during the outage is printed, so two prints in total: the first job is not
   /// printed a second time just because its receipt had to be replayed.
   /// </summary>
   [Fact]
   public async Task ReconnectReplaysLostSpoolReceiptAndDrainsBufferedJobsWithoutPrintingAgain()
   {
      await using var fixture = new RealtimeRuntimeFixture();
      RealtimeRuntimeFixture.Connection first = await fixture.ConnectAsync(loseSpoolAck: true);
      fixture.AddJob("first");
      await first.SendAsync("JOB_AVAILABLE", new { });
      await first.LostSpoolAck.Task.WaitAsync(TimeSpan.FromSeconds(5));
      fixture.AddJob("buffered");
      _ = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => string.Equals(fixture.State("buffered"), "SPOOLED", StringComparison.Ordinal));
      Assert.Equal(2, fixture.Printer.Prints);
      Assert.Equal("SPOOLED", fixture.State("first"));
      string[] firstReceipts = fixture.Messages.Where(message => message.Type is "JOB_EVENT"
         && message.Payload.GetProperty("jobId").GetString() is "first"
         && message.Payload.GetProperty("event").GetProperty("status").GetString() is "SPOOLED").Select(message => message.MessageId).ToArray();
      Assert.Equal(2, firstReceipts.Length);
      Assert.Equal(firstReceipts[0], firstReceipts[1]);
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>A read-only stream whose first read signals the test and then never completes until cancelled, simulating a document download that stalls.</summary>
   private sealed class StalledDocument : Stream
   {
      internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);



      /// <summary>Readable.</summary>
      public override bool CanRead => true;



      /// <summary>Not seekable, as a network body is not.</summary>
      public override bool CanSeek => false;



      /// <summary>Not writable.</summary>
      public override bool CanWrite => false;



      /// <summary>Unknown length, as for a chunked response.</summary>
      public override long Length => throw new NotSupportedException();



      /// <summary>Position is unsupported on a forward-only network body.</summary>
      public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }



      /// <summary>Signals that the download reached the body, then waits until cancelled.</summary>
      public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
      {
         Entered.TrySetResult();
         await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
         return 0;
      }



      /// <summary>Synchronous reads are unsupported; the download must be asynchronous.</summary>
      public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();



      /// <summary>Unsupported.</summary>
      public override void Flush() => throw new NotSupportedException();



      /// <summary>Unsupported.</summary>
      public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();



      /// <summary>Unsupported.</summary>
      public override void SetLength(long value) => throw new NotSupportedException();



      /// <summary>Unsupported.</summary>
      public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
   }
}
