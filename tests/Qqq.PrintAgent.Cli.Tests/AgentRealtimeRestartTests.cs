/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// What the service does at start with work a previous run left unfinished. Whether paper came out of a submission that was
/// in progress is not something the service can know, so it reports the uncertainty and never prints the job again by itself.
/// </summary>
public sealed class AgentRealtimeRestartTests
{
   /// <summary>
   /// The journal holds one job that was being submitted to Windows when the service died and one that had only been received.
   /// At start, before any connection, the first is recorded OUTCOME_UNKNOWN and the second FAILED. Once connected both
   /// outcomes are reported to the server, with the advice to inspect the printer for the uncertain one. The server then hands
   /// the uncertain job out again, as it does until a claim is confirmed; the service takes the claim and does not print it.
   /// </summary>
   [Fact]
   public async Task WorkInterruptedByACrashIsReportedAndNeverPrintedAgain()
   {
      await using RealtimeRuntimeFixture fixture = new(seed: store =>
      {
         SeedInterrupted(store, "submitting", "RECEIVED", "SUBMITTING");
         SeedInterrupted(store, "received", "RECEIVED");
      });
      Assert.Equal("OUTCOME_UNKNOWN", fixture.State("submitting"));
      Assert.Equal("FAILED", fixture.State("received"));
      fixture.AddJob("submitting");
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => Reported(fixture, "submitting", "OUTCOME_UNKNOWN") is not null && Reported(fixture, "received", "FAILED") is not null);
      Assert.Contains("inspect the printer", Reported(fixture, "submitting", "OUTCOME_UNKNOWN"), StringComparison.Ordinal);
      Assert.Contains("before native submission", Reported(fixture, "received", "FAILED"), StringComparison.Ordinal);
      await fixture.WaitAsync(fixture.ClaimArrived.Task);
      int claims = fixture.Claims;
      await connection.SendAsync("JOB_AVAILABLE", new { });
      // Claims are made one at a time, so a later claim means the replayed one has been fully handled.
      await fixture.WaitAsync(() => fixture.Claims > claims);
      Assert.Equal(0, fixture.Printer.Prints);
      Assert.Equal("OUTCOME_UNKNOWN", fixture.State("submitting"));
      Assert.Equal(1, fixture.Messages.Count(message => message.Type is "JOB_EVENT" && message.Payload.GetProperty("jobId").GetString() is "submitting"));
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>
   /// A submission is inside the printer when the service is stopped. SUBMITTING had been recorded and acknowledged by the
   /// server before the printer was called, and the stop turns it into OUTCOME_UNKNOWN, not FAILED and not a silent retry. After
   /// the restart the outcome is reported, and when the server hands the job out again the service does not print it.
   /// </summary>
   [Fact]
   public async Task SubmissionInterruptedByAServiceStopIsOutcomeUnknownAndNeverPrintedAgain()
   {
      await using RealtimeRuntimeFixture fixture = new();
      fixture.Printer.BlockPrint = true;
      RealtimeRuntimeFixture.Connection first = await fixture.ConnectAsync();
      fixture.AddJob("job");
      await first.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(fixture.Printer.Entered.Task);
      Assert.Equal("SUBMITTING", fixture.State("job"));
      Assert.Contains(fixture.Messages, message => IsEvent(message, "job", "SUBMITTING"));
      await fixture.RestartAsync();
      Assert.Equal("OUTCOME_UNKNOWN", fixture.State("job"));
      int claims = fixture.Claims;
      fixture.AddJob("job");
      RealtimeRuntimeFixture.Connection second = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => Reported(fixture, "job", "OUTCOME_UNKNOWN") is not null);
      await fixture.WaitAsync(() => fixture.Claims > claims);
      await second.SendAsync("JOB_AVAILABLE", new { });
      // Claims are made one at a time, so a second claim means the replayed one has been fully handled.
      await fixture.WaitAsync(() => fixture.Claims > claims + 1);
      Assert.Equal(0, fixture.Printer.Prints);
      Assert.Equal("OUTCOME_UNKNOWN", fixture.State("job"));
      Assert.DoesNotContain(fixture.Messages, message => IsEvent(message, "job", "SPOOLED") || IsEvent(message, "job", "FAILED"));
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>Writes the journal a crashed service leaves behind for one job: it was claimed and the listed states were recorded and delivered, the last of them being where the service stopped.</summary>
   private static void SeedInterrupted(AgentStore store, string jobId, params string[] states)
   {
      store.TrackJob(new PrintJob(jobId, "printer", "Synthetic " + jobId, "PDF", "unused", 1, new PrintOptions(), RealtimeRuntimeFixture.Now(), "CLAIMED"), "Synthetic");
      foreach(string state in states)
      {
         PrintEvent recorded = new(jobId + "-" + state, state);
         store.Record(jobId, recorded);
         store.Acknowledge(recorded.EventId);
      }
   }



   /// <summary>The message of the event the agent reported for a job with the given status, or null when it has not reported one.</summary>
   private static string? Reported(RealtimeRuntimeFixture fixture, string jobId, string status)
      => fixture.Messages.Where(message => IsEvent(message, jobId, status))
         .Select(message => message.Payload.GetProperty("event").GetProperty("message").GetString()).FirstOrDefault();



   /// <summary>True for a job event message of the given job and status.</summary>
   private static bool IsEvent(RealtimeEnvelope message, string jobId, string status)
      => message.Type is "JOB_EVENT" && string.Equals(message.Payload.GetProperty("jobId").GetString(), jobId, StringComparison.Ordinal)
         && string.Equals(message.Payload.GetProperty("event").GetProperty("status").GetString(), status, StringComparison.Ordinal);
}
