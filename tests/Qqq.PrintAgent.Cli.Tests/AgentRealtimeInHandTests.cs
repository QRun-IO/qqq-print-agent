/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

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
   /// One lane has claimed a job and is inside the printer with it, so SUBMITTING is recorded and the lane holds the
   /// job's ticket. The server then offers the same job to a second lane. The second lane says so once in the log,
   /// records and reports nothing, and ends its pass. It does not register a second ticket for the job, which would
   /// stop the lane with an error ("An item with the same key has already been added"), and it does not print. When
   /// the printer lets the first submission finish, the job is SPOOLED and was printed once, with one RECEIVED, one
   /// SUBMITTING and one SPOOLED reported. The second lane's claim is held at the server until the first submission is
   /// inside the printer, so the offer arrives while the submission is held.
   /// </summary>
   [Fact]
   public async Task JobOfferedAgainWhileItsSubmissionIsHeldIsLeftToTheLaneThatHoldsIt()
   {
      await using RealtimeRuntimeFixture fixture = new(downloads: 2);
      fixture.Printer.BlockPrint = true;
      var secondClaim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      fixture.BeforeAcknowledgement = message =>
      {
         ////////////////////////////////////////////////////////////////////////
         // the next lane claims as soon as this RECEIVED is acknowledged, so  //
         // its claim is held from here until the first lane is in the printer //
         ////////////////////////////////////////////////////////////////////////
         if(IsEvent(message, "RECEIVED"))
         { fixture.ClaimHold = secondClaim; }
         return Task.CompletedTask;
      };
      fixture.AddJob("job");
      await fixture.ConnectAsync();
      await fixture.WaitAsync(fixture.Printer.Entered.Task);
      await fixture.WaitAsync(() => fixture.Claims == 2);
      Assert.Equal("SUBMITTING", fixture.State("job"));

      fixture.AddJob("job");
      secondClaim.SetResult();
      await fixture.WaitAsync(() => fixture.Logs.Any(line => line.Contains("offered again", StringComparison.Ordinal)));
      Assert.Contains(InHandLine, Assert.Single(fixture.Logs, line => line.Contains("offered again", StringComparison.Ordinal)), StringComparison.Ordinal);
      Assert.Equal("SUBMITTING", fixture.State("job"));
      Assert.Equal(1, fixture.Printer.Submissions);

      fixture.Printer.Release.SetResult();
      await fixture.WaitAsync(() => fixture.Messages.Any(message => IsEvent(message, "SPOOLED")));
      Assert.Equal("SPOOLED", fixture.State("job"));
      Assert.Equal("job", Assert.Single(fixture.Printer.Order));
      Assert.Equal(1, fixture.Printer.Submissions);
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "SPOOLED" }, fixture.Messages.Where(message => message.Type is "JOB_EVENT").Select(message => message.Payload.GetProperty("event").GetProperty("status").GetString()), StringComparer.Ordinal);
      Assert.Single(fixture.Logs, line => line.Contains("offered again", StringComparison.Ordinal));
      Assert.DoesNotContain(fixture.Logs, line => line.Contains("Print lane stopped", StringComparison.Ordinal) || line.Contains("same key", StringComparison.OrdinalIgnoreCase));
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>True for an event of the test's one job with the given status.</summary>
   private static bool IsEvent(RealtimeEnvelope message, string status)
      => message.Type is "JOB_EVENT" && string.Equals(message.Payload.GetProperty("jobId").GetString(), "job", StringComparison.Ordinal)
         && string.Equals(message.Payload.GetProperty("event").GetProperty("status").GetString(), status, StringComparison.Ordinal);
}
