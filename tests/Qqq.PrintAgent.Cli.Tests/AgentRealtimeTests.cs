using System.Globalization;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentRealtimeTests
{
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
      int claims = fixture.Claims;
      await Task.Delay(250);
      Assert.Equal(claims, fixture.Claims);
      Assert.Equal(0, fixture.ForbiddenPolls);
   }

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

   private sealed class StalledDocument : Stream
   {
      internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      public override bool CanRead => true;
      public override bool CanSeek => false;
      public override bool CanWrite => false;
      public override long Length => throw new NotSupportedException();
      public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
      public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
      {
         Entered.TrySetResult();
         await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
         return 0;
      }
      public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
      public override void Flush() => throw new NotSupportedException();
      public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
      public override void SetLength(long value) => throw new NotSupportedException();
      public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
   }
}
