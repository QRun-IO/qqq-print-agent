/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// What the service does when the server offers a job that one of its own lanes still holds. The lane that holds the
/// job is the only one that may act on it, so the offer is logged in plain words and otherwise ignored.
/// </summary>
public sealed class AgentRealtimeInHandTests
{
   private const string InHandLine = "job: offered again while a lane of this computer still holds it, working on it or just finished with it; it is left to that lane, nothing is recorded for it and this pass claims nothing more.";



   /// <summary>
   /// One lane has claimed a job and holds its ticket, and the server then offers the same job to a second lane. In
   /// the first row the first lane is still downloading the document, so the journal says RECEIVED and only the
   /// ticket shows that the job is in hand; in the second it is inside the printer, so SUBMITTING is recorded as
   /// well. Either way the second lane says so once in the log, in the words for a job in hand, records and reports
   /// nothing, and ends its pass. It does not register a second ticket for the job, which would stop the lane with an
   /// error ("An item with the same key has already been added"), and it does not print. When the first lane is let
   /// go, the job is SPOOLED and was printed once, with one RECEIVED, one SUBMITTING and one SPOOLED reported.
   /// <para>
   /// Only one download is allowed at first, so no second lane can start a pass, read the journal or claim until the
   /// first lane is held where the row wants it. The test then puts the job back on the server's queue and has the
   /// server allow a second download, which is what lets the second lane claim. The wait that follows ends when that
   /// lane's pass has ended, in either of the two ways it can: with the line for an offer, or with a stopped lane.
   /// </para>
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task JobOfferedAgainWhileALaneHoldsItIsLeftToThatLane(bool submitting)
   {
      await using RealtimeRuntimeFixture fixture = new(downloads: 1);
      byte[] bytes = Encoding.ASCII.GetBytes("%PDF-synthetic-job");
      using var document = new HeldDocument(bytes);
      fixture.Printer.BlockPrint = submitting;
      if(submitting)
      { document.Release.SetResult(); }
      fixture.AddJob("job", () => document, length: bytes.Length);
      Assert.True(fixture.Queue.TryPeek(out PrintJob? offer));
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync();
      await fixture.WaitAsync(submitting ? fixture.Printer.Entered.Task : document.Entered.Task);
      string held = submitting ? "SUBMITTING" : "RECEIVED";
      Assert.Equal(held, fixture.State("job"));
      Assert.Equal(1, fixture.Claims);

      fixture.Queue.Enqueue(offer);
      await connection.SendAsync("SETTINGS", fixture.Settings with { Revision = 2, MaxConcurrentDownloads = 2 });
      await fixture.WaitAsync(() => fixture.Logs.Any(line => IsOffer(line) || IsStoppedLane(line)));
      Assert.Contains(InHandLine, Assert.Single(fixture.Logs, IsOffer), StringComparison.Ordinal);
      Assert.Equal(2, fixture.Claims);
      Assert.Equal(held, fixture.State("job"));
      Assert.Equal(submitting ? 1 : 0, fixture.Printer.Submissions);

      document.Release.TrySetResult();
      fixture.Printer.Release.SetResult();
      await fixture.WaitAsync(() => fixture.Messages.Any(message => IsEvent(message, "SPOOLED")));
      Assert.Equal("SPOOLED", fixture.State("job"));
      Assert.Equal("job", Assert.Single(fixture.Printer.Order));
      Assert.Equal(1, fixture.Printer.Submissions);
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "SPOOLED" }, fixture.Messages.Where(message => message.Type is "JOB_EVENT").Select(message => message.Payload.GetProperty("event").GetProperty("status").GetString()), StringComparer.Ordinal);
      Assert.Single(fixture.Logs, IsOffer);
      Assert.DoesNotContain(fixture.Logs, IsStoppedLane);
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>True for a log line about a job that was offered again, whatever reason the line gives.</summary>
   private static bool IsOffer(string line) => line.Contains("offered again", StringComparison.Ordinal);



   /// <summary>True for a log line that says a lane stopped on an error, or that carries the duplicate-ticket error itself.</summary>
   private static bool IsStoppedLane(string line) => line.Contains("Print lane stopped", StringComparison.Ordinal) || line.Contains("same key", StringComparison.OrdinalIgnoreCase);



   /// <summary>True for an event of the test's one job with the given status.</summary>
   private static bool IsEvent(RealtimeEnvelope message, string status)
      => message.Type is "JOB_EVENT" && string.Equals(message.Payload.GetProperty("jobId").GetString(), "job", StringComparison.Ordinal)
         && string.Equals(message.Payload.GetProperty("event").GetProperty("status").GetString(), status, StringComparison.Ordinal);



   /// <summary>A document body that signals when the agent starts reading it and then withholds its bytes until the test releases it, which holds a lane in its download with the job in hand.</summary>
   private sealed class HeldDocument(byte[] bytes) : MemoryStream(bytes)
   {
      internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);



      /// <summary>Signals the first read, waits for release, then reads normally.</summary>
      public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
      {
         Entered.TrySetResult();
         await Release.Task.WaitAsync(cancellationToken);
         return await base.ReadAsync(buffer, cancellationToken);
      }
   }
}
