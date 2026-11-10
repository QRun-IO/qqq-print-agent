/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Verifies how the running service quiesces itself so an MSI update never lands in the middle of a job, and never loses one that was already claimed.</summary>
public sealed class AgentRuntimeUpdateTests
{
   private const string Quiesced = "Printing paused for an application update after active submission finished.";
   private const string Paused = "Printing paused for application update.";



   /// <summary>
   /// While a submission is held inside the printer, prepare-update applies the pause at once but answers only after that
   /// submission has finished and been recorded. Once ready, a job the server announces is shown as queued but not claimed,
   /// while a heartbeat interval passes and the heartbeat is still sent, so the server keeps seeing the machine as alive.
   /// Resuming claims and prints the waiting job. The claims after the resume are counted exactly: the first is held at the
   /// server while one more wake-up is delivered, so the drain in progress makes the job claim and an empty one, the single
   /// pending drain makes one empty claim, and a later announcement adds exactly two. Any claim made during the pause would
   /// break that count.
   /// </summary>
   [Fact]
   public async Task UpdatePreparationDrainsActiveWorkThenPausesClaimsButKeepsHeartbeats()
   {
      var clock = new ManualTimeProvider();
      await using RealtimeRuntimeFixture fixture = new(timeProvider: clock);
      fixture.Printer.BlockPrint = true;
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      fixture.AddJob("active");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(fixture.Printer.Entered.Task);
      Task<string> preparation = fixture.PipeAsync(new PipeRequest("prepare-update"));
      await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Status.StartsWith(Paused, StringComparison.Ordinal));
      // The pause is in force and the submission is still inside the printer, so the request must still be waiting for it.
      Assert.False(preparation.IsCompleted);
      Assert.DoesNotContain(Quiesced, fixture.Logs, StringComparer.Ordinal);
      Assert.Equal("SUBMITTING", fixture.State("active"));
      fixture.Printer.Release.TrySetResult();
      Assert.Equal("Ready for update.", await preparation);
      Assert.Equal("SPOOLED", fixture.State("active"));
      Assert.Contains(Quiesced, fixture.Logs, StringComparer.Ordinal);
      int claims = fixture.Claims;
      int heartbeats = fixture.Count("HEARTBEAT");
      fixture.AddJob("waiting");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Jobs.Any(job => job.JobId is "waiting" && job.Status is "QUEUED"));
      await fixture.HeartbeatAsync(clock);
      await fixture.WaitAsync(() => fixture.Count("HEARTBEAT") > heartbeats);
      Assert.Equal(claims, fixture.Claims);
      Assert.Null(fixture.State("waiting"));
      fixture.ClaimHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      Assert.Equal("Printing resumed.", await fixture.PipeAsync(new PipeRequest("resume-after-update")));
      await fixture.WaitAsync(() => fixture.Claims == claims + 1);
      await connection.SendAsync("JOB_AVAILABLE", new { });
      // Frames are handled in order, so the receipt of this later command proves the announcement before it was handled.
      await connection.SendAsync("COMMAND", RealtimeRuntimeFixture.Command(id: "marker", expired: true));
      await fixture.WaitAsync(() => fixture.Count("COMMAND_RECEIPT") == 1);
      fixture.ClaimHold.SetResult();
      await fixture.WaitAsync(() => fixture.State("waiting") is "SPOOLED" && fixture.Claims == claims + 3);
      fixture.AddJob("late");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.State("late") is "SPOOLED" && fixture.LastClaimWasEmpty);
      Assert.Equal(claims + 5, fixture.Claims);
      Assert.Equal(new[] { "active", "waiting", "late" }, fixture.Printer.Order, StringComparer.Ordinal);
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>
   /// A job is claimed and its document is still downloading when prepare-update arrives. The request does not answer while
   /// the download is held, although nothing is printing yet. When the download finishes, the job passes the submission gate
   /// in spite of the pause, is printed and reported, and only then does the reply say the service is ready. A job announced
   /// during the pause is not claimed until printing is resumed.
   /// </summary>
   [Fact]
   public async Task UpdatePreparationLetsAJobThatIsStillDownloadingFinishBeforeItAnswers()
   {
      await using RealtimeRuntimeFixture fixture = new(timeProvider: new ManualTimeProvider());
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      byte[] bytes = Encoding.ASCII.GetBytes("%PDF-synthetic-downloading");
      using var document = new GatedDocument(bytes);
      fixture.AddJob("downloading", () => document, length: bytes.Length);
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(document.Entered.Task);
      try
      {
         int claims = fixture.Claims;
         Task<string> preparation = fixture.PipeAsync(new PipeRequest("prepare-update"));
         await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Status.StartsWith(Paused, StringComparison.Ordinal));
         Assert.False(preparation.IsCompleted);
         Assert.Equal("RECEIVED", fixture.State("downloading"));
         fixture.AddJob("announced");
         await connection.SendAsync("JOB_AVAILABLE", new { });
         await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Jobs.Any(job => job.JobId is "announced" && job.Status is "QUEUED"));
         Assert.False(preparation.IsCompleted);
         document.Release.TrySetResult();
         Assert.Equal("Ready for update.", await preparation);
         Assert.Equal("SPOOLED", fixture.State("downloading"));
         Assert.Contains(fixture.Messages, message => IsEvent(message, "downloading", "SPOOLED"));
         Assert.Equal(1, fixture.Printer.Prints);
         Assert.Equal(claims, fixture.Claims);
         Assert.Null(fixture.State("announced"));
         Assert.Equal("Printing resumed.", await fixture.PipeAsync(new PipeRequest("resume-after-update")));
         await fixture.WaitAsync(() => fixture.State("announced") is "SPOOLED");
         Assert.Equal(0, fixture.ForbiddenPolls);
      }
      finally { document.Release.TrySetResult(); }
   }



   /// <summary>
   /// A claim is on its way to the server when prepare-update arrives, and the server answers it with a job afterwards. That
   /// job was claimed before the pause, so the request waits for it: it is printed and reported before the reply says ready.
   /// </summary>
   [Fact]
   public async Task UpdatePreparationWaitsForAClaimInFlightAndPrintsItsJob()
   {
      await using RealtimeRuntimeFixture fixture = new(timeProvider: new ManualTimeProvider());
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => fixture.LastClaimWasEmpty);
      int claims = fixture.Claims;
      fixture.ClaimHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      fixture.AddJob("claimed");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Claims > claims);
      Task<string> preparation = fixture.PipeAsync(new PipeRequest("prepare-update"));
      await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Status.StartsWith(Paused, StringComparison.Ordinal));
      Assert.False(preparation.IsCompleted);
      fixture.ClaimHold.SetResult();
      Assert.Equal("Ready for update.", await preparation);
      Assert.Equal("SPOOLED", fixture.State("claimed"));
      Assert.Contains(fixture.Messages, message => IsEvent(message, "claimed", "SPOOLED"));
      Assert.Equal(1, fixture.Printer.Prints);
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>
   /// The wait for claimed work is bounded. With a download held open, the preparation limit passes on the manual clock and
   /// the reply says the service is not ready and how many jobs were still in progress; the log says the same. The pause
   /// stays in force. The held job is not given up: once its download finishes it is printed, although the pause is still on.
   /// </summary>
   [Fact]
   public async Task UpdatePreparationSaysSoWhenClaimedWorkOutlastsItsLimit()
   {
      var clock = new ManualTimeProvider();
      await using RealtimeRuntimeFixture fixture = new(timeProvider: clock);
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      byte[] bytes = Encoding.ASCII.GetBytes("%PDF-synthetic-slow");
      using var document = new GatedDocument(bytes);
      fixture.AddJob("slow", () => document, length: bytes.Length);
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(document.Entered.Task);
      try
      {
         Task<string> preparation = fixture.PipeAsync(new PipeRequest("prepare-update"));
         await fixture.WaitAsync(() => clock.HasTimerDueIn(AgentRuntime.UpdatePreparationLimit));
         Assert.False(preparation.IsCompleted);
         clock.Advance(AgentRuntime.UpdatePreparationLimit);
         string reply = await preparation;
         Assert.StartsWith("Not ready for update: 1 job(s) were still in progress", reply, StringComparison.Ordinal);
         Assert.Equal("The update was not started, and the current version remains installed. " + reply, MsiUpdateService.PreparationNotCompleted(reply));
         Assert.Contains(fixture.Logs, line => line.StartsWith("Update preparation stopped waiting", StringComparison.Ordinal) && line.Contains("1 job(s)", StringComparison.Ordinal));
         Assert.DoesNotContain(Quiesced, fixture.Logs, StringComparer.Ordinal);
         Assert.StartsWith(Paused, fixture.Runtime.GetSnapshot().Status, StringComparison.Ordinal);
         Assert.Equal("RECEIVED", fixture.State("slow"));
         document.Release.TrySetResult();
         await fixture.WaitAsync(() => fixture.State("slow") is "SPOOLED");
         Assert.StartsWith(Paused, fixture.Runtime.GetSnapshot().Status, StringComparison.Ordinal);
         Assert.Equal("Printing resumed.", await fixture.PipeAsync(new PipeRequest("resume-after-update")));
      }
      finally { document.Release.TrySetResult(); }
   }



   /// <summary>
   /// The limit runs out while the server has still not answered a claim that was sent before the pause, and no job is in
   /// progress. The reply and the log say that a claim was still in progress, not that no jobs were. When the server then
   /// answers the claim with a job, that job is printed although the pause is still in force.
   /// </summary>
   [Fact]
   public async Task UpdatePreparationSaysAClaimWasStillInProgressWhenItsLimitRunsOut()
   {
      var clock = new ManualTimeProvider();
      await using RealtimeRuntimeFixture fixture = new(timeProvider: clock);
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => fixture.LastClaimWasEmpty);
      int claims = fixture.Claims;
      fixture.ClaimHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      fixture.AddJob("claimed");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Claims > claims);
      Task<string> preparation = fixture.PipeAsync(new PipeRequest("prepare-update"));
      await fixture.WaitAsync(() => clock.HasTimerDueIn(AgentRuntime.UpdatePreparationLimit));
      clock.Advance(AgentRuntime.UpdatePreparationLimit);
      string reply = await preparation;
      Assert.StartsWith("Not ready for update: a claim was still in progress after 25 seconds.", reply, StringComparison.Ordinal);
      Assert.DoesNotContain("job(s)", reply, StringComparison.Ordinal);
      Assert.Contains(fixture.Logs, line => line.StartsWith("Update preparation stopped waiting after 25 s: a claim was still in progress", StringComparison.Ordinal));
      Assert.StartsWith(Paused, fixture.Runtime.GetSnapshot().Status, StringComparison.Ordinal);
      fixture.ClaimHold.SetResult();
      await fixture.WaitAsync(() => fixture.State("claimed") is "SPOOLED");
      Assert.StartsWith(Paused, fixture.Runtime.GetSnapshot().Status, StringComparison.Ordinal);
      Assert.Equal("Printing resumed.", await fixture.PipeAsync(new PipeRequest("resume-after-update")));
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>
   /// The server sends a new desired state, or new settings, while update preparation is waiting for a job whose document is
   /// still downloading. The frame is saved and acknowledged during the wait, so the runtime becomes ready again; the job can
   /// then pass the submission gate, is printed and reported, and the reply is "Ready for update." Were the frame left
   /// unapplied until preparation ended, the job could not be submitted and preparation would wait for work it was itself
   /// holding up.
   /// </summary>
   [Theory]
   [InlineData("DESIRED_STATE")]
   [InlineData("SETTINGS")]
   public async Task ServerStateArrivingDuringUpdatePreparationIsAppliedAndTheClaimedJobFinishes(string frame)
   {
      await using RealtimeRuntimeFixture fixture = new(timeProvider: new ManualTimeProvider());
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      byte[] bytes = Encoding.ASCII.GetBytes("%PDF-synthetic-during");
      using var document = new GatedDocument(bytes);
      fixture.AddJob("during", () => document, length: bytes.Length);
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(document.Entered.Task);
      try
      {
         Task<string> preparation = fixture.PipeAsync(new PipeRequest("prepare-update"));
         await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Status.StartsWith(Paused, StringComparison.Ordinal));
         int applied = fixture.Count("STATE_APPLIED");
         if(frame is "SETTINGS")
         { await connection.SendAsync("SETTINGS", fixture.Settings with { Revision = 2 }); }
         else
         { await connection.SendAsync("DESIRED_STATE", new DeviceDesiredState(2, false, "CONNECTED")); }
         // Saved and acknowledged while preparation is still waiting for the download.
         await fixture.WaitAsync(() => fixture.Count("STATE_APPLIED") > applied && fixture.Runtime.GetSnapshot().Connected);
         Assert.False(preparation.IsCompleted);
         Assert.Equal("RECEIVED", fixture.State("during"));
         document.Release.TrySetResult();
         Assert.Equal("Ready for update.", await preparation);
         Assert.Equal("SPOOLED", fixture.State("during"));
         Assert.Contains(fixture.Messages, message => IsEvent(message, "during", "SPOOLED"));
         Assert.Equal("Printing resumed.", await fixture.PipeAsync(new PipeRequest("resume-after-update")));
         Assert.Equal(0, fixture.ForbiddenPolls);
      }
      finally { document.Release.TrySetResult(); }
   }



   /// <summary>
   /// Printing is resumed by another command while preparation is still waiting for a job whose document is downloading.
   /// The pause is then no longer in force, so when the job has finished the reply is not "Ready for update.": it says that
   /// preparation was interrupted and must be repeated, and the log says the same. The management pipe of this fixture
   /// serves one command at a time, so both operations are called on the runtime directly, as a pipe that serves several
   /// will call them.
   /// </summary>
   [Fact]
   public async Task UpdatePreparationInterruptedByAResumeDoesNotAnswerReady()
   {
      await using RealtimeRuntimeFixture fixture = new(timeProvider: new ManualTimeProvider());
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      byte[] bytes = Encoding.ASCII.GetBytes("%PDF-synthetic-resumed");
      using var document = new GatedDocument(bytes);
      fixture.AddJob("resumed", () => document, length: bytes.Length);
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(document.Entered.Task);
      try
      {
         Task<string> preparation = fixture.Runtime.PrepareUpdateAsync(CancellationToken.None);
         await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Status.StartsWith(Paused, StringComparison.Ordinal));
         Assert.Equal("Printing resumed.", fixture.Runtime.ResumeAfterUpdate());
         Assert.False(preparation.IsCompleted);
         document.Release.TrySetResult();
         await fixture.WaitAsync(preparation);
         string reply = await preparation;
         Assert.StartsWith("Update preparation was interrupted: printing was resumed", reply, StringComparison.Ordinal);
         Assert.Equal("The update was not started, and the current version remains installed. " + reply, MsiUpdateService.PreparationNotCompleted(reply));
         Assert.EndsWith("Try the update again.", reply, StringComparison.Ordinal);
         Assert.Contains(fixture.Logs, line => line.StartsWith("Update preparation was interrupted", StringComparison.Ordinal));
         Assert.DoesNotContain(Quiesced, fixture.Logs, StringComparer.Ordinal);
         Assert.Equal("SPOOLED", fixture.State("resumed"));
         Assert.Equal(0, fixture.ForbiddenPolls);
      }
      finally { document.Release.TrySetResult(); }
   }



   /// <summary>
   /// A sign-in begins while preparation is waiting for a job whose document is downloading. The sign-in is a connection
   /// change: it cancels the job, which ends preparation's wait, and it is still under way (its first request to the server
   /// is held open) when preparation decides its answer. The pause is in force, but the connection is being changed, so the
   /// reply is not "Ready for update.": it says that preparation was interrupted and must be repeated.
   /// </summary>
   [Fact]
   public async Task UpdatePreparationInterruptedByASignInDoesNotAnswerReady()
   {
      using var signInReachedTheServer = new ManualResetEventSlim();
      using var letSignInContinue = new ManualResetEventSlim();
      await using RealtimeRuntimeFixture fixture = new(timeProvider: new ManualTimeProvider());
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      byte[] bytes = Encoding.ASCII.GetBytes("%PDF-synthetic-sign-in");
      using var document = new GatedDocument(bytes);
      fixture.AddJob("cancelled", () => document, length: bytes.Length);
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(document.Entered.Task);
      try
      {
         Task<string> preparation = fixture.Runtime.PrepareUpdateAsync(CancellationToken.None);
         await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Status.StartsWith(Paused, StringComparison.Ordinal));
         fixture.HttpFault = request =>
         {
            if(string.Equals(request.RequestUri!.AbsolutePath, "/qqq/v1/manageSession", StringComparison.Ordinal))
            {
               signInReachedTheServer.Set();
               letSignInContinue.Wait();
            }
            return null;
         };
         Task<string> signIn = Task.Run(() => fixture.Runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: new QqqSessionCredential(fixture.Peer.Origin, "synthetic-session")), CancellationToken.None));
         await fixture.WaitAsync(() => signInReachedTheServer.IsSet);
         await fixture.WaitAsync(preparation);
         string reply = await preparation;
         Assert.StartsWith("Update preparation was interrupted: a sign-in, sign-out or enrollment began", reply, StringComparison.Ordinal);
         Assert.DoesNotContain(Quiesced, fixture.Logs, StringComparer.Ordinal);
         Assert.StartsWith(Paused, fixture.Runtime.GetSnapshot().Status, StringComparison.Ordinal);
         letSignInContinue.Set();
         await fixture.WaitAsync(signIn);
         Assert.Equal(0, fixture.Printer.Submissions);
      }
      finally
      {
         letSignInContinue.Set();
         document.Release.TrySetResult();
      }
   }



   /// <summary>
   /// An updater that never comes back cannot leave printing off. A job announced during the pause is not claimed while the
   /// pause lasts, across a heartbeat interval. When the pause's ten minutes have passed on the manual clock, with no resume,
   /// the next heartbeat round notices, the status no longer says paused, and the waiting job is claimed and printed.
   /// </summary>
   [Fact]
   public async Task UpdatePauseExpiresByItselfAndTheWaitingJobIsPrinted()
   {
      var clock = new ManualTimeProvider();
      await using RealtimeRuntimeFixture fixture = new(timeProvider: clock);
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      Assert.Equal("Ready for update.", await fixture.PipeAsync(new PipeRequest("prepare-update")));
      int heartbeats = fixture.Count("HEARTBEAT");
      fixture.AddJob("waiting");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Jobs.Any(job => job.JobId is "waiting" && job.Status is "QUEUED"));
      await fixture.HeartbeatAsync(clock);
      await fixture.WaitAsync(() => fixture.Count("HEARTBEAT") > heartbeats);
      Assert.StartsWith(Paused, fixture.Runtime.GetSnapshot().Status, StringComparison.Ordinal);
      Assert.Null(fixture.State("waiting"));
      clock.Advance(AgentRuntime.UpdatePauseDuration);
      await fixture.WaitAsync(() => fixture.State("waiting") is "SPOOLED");
      Assert.Contains(fixture.Logs, line => line.StartsWith("The update pause ran out", StringComparison.Ordinal));
      Assert.False(fixture.Runtime.GetSnapshot().Status.StartsWith(Paused, StringComparison.Ordinal));
      Assert.Equal(1, fixture.Printer.Prints);
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>True for a job event message of the given job and status.</summary>
   private static bool IsEvent(RealtimeEnvelope message, string jobId, string status)
      => message.Type is "JOB_EVENT" && string.Equals(message.Payload.GetProperty("jobId").GetString(), jobId, StringComparison.Ordinal)
         && string.Equals(message.Payload.GetProperty("event").GetProperty("status").GetString(), status, StringComparison.Ordinal);



   /// <summary>A document body that signals when the agent starts reading it and then withholds its bytes until the test releases it, holding a download open at a precise point.</summary>
   private sealed class GatedDocument(byte[] bytes) : MemoryStream(bytes)
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
