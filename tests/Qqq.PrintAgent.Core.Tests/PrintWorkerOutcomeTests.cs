/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// What the worker records when a job does not simply spool. The line it must never blur is whether bytes may have
/// reached a printer: a refusal before native submission is FAILED and safe to submit again; anything that goes wrong
/// after SUBMITTING was recorded is OUTCOME_UNKNOWN and is never printed again automatically. The hooks the realtime
/// runtime supplies (staging, leases, gates, throttling, live settings) are exercised here with hand-written fakes.
/// </summary>
public sealed class PrintWorkerOutcomeTests
{
   private static readonly byte[] Document = [1, 2, 3, 4, 5];



   /// <summary>
   /// A native submission that fails or times out after SUBMITTING was recorded may have printed, so the job becomes
   /// OUTCOME_UNKNOWN with the reason and no spooler receipt. When the server then offers the same job again, the worker
   /// neither downloads nor submits it and reports nothing new: one submission, ever.
   /// </summary>
   [Theory]
   [InlineData(true, "The printer operation timed out. Inspect the printer and job outcome before retrying.")]
   [InlineData(false, "The spooler connection was lost.")]
   public async Task SubmissionInterruptedAfterSubmittingIsUnknownAndNeverReprinted(bool timedOut, string reason)
   {
      PrintJob job = FakePrintServer.Job(Document);
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.Offer(job, Document).Offer(job, Document);
      printer.Printing = (_, _, _) => throw (timedOut ? new TimeoutException(reason) : new IOException(reason));

      Assert.True(await harness.RunOnceAsync());
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "OUTCOME_UNKNOWN" }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal(reason, journal.Last("job-1").Message);
      Assert.Null(journal.Last("job-1").SpoolerJobId);
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "OUTCOME_UNKNOWN" }, server.States, StringComparer.Ordinal);
      Assert.Equal(1, printer.Submissions);

      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(2, server.Claims);
      Assert.Equal(1, server.Downloads);
      Assert.Equal(1, printer.Submissions);
      Assert.Equal(3, server.Events.Count);
      Assert.Equal("OUTCOME_UNKNOWN", journal.GetState("job-1"));
   }



   /// <summary>
   /// A submission cancelled part-way (a service stop, an operator cancel) is just as uncertain. The outcome is recorded
   /// at once, held in the journal while the worker is stopping, delivered by the next run, and the job is not
   /// printed again when the server offers it.
   /// </summary>
   [Fact]
   public async Task SubmissionCancelledPartWayIsUnknownAndReportedByTheNextRun()
   {
      PrintJob job = FakePrintServer.Job(Document);
      using var stop = new CancellationTokenSource();
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.Offer(job, Document).Offer(job, Document);
      printer.Printing = async (_, _, token) =>
      {
         await stop.CancelAsync();
         token.ThrowIfCancellationRequested();
         return "never-returned";
      };

      Assert.True(await harness.RunOnceAsync(stop.Token));
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "OUTCOME_UNKNOWN" }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING" }, server.States, StringComparer.Ordinal);
      Assert.Equal("OUTCOME_UNKNOWN", Assert.Single(journal.Pending()).Event.Status);

      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "OUTCOME_UNKNOWN" }, server.States, StringComparer.Ordinal);
      Assert.Empty(journal.Pending());
      Assert.Equal(1, printer.Submissions);
   }



   /// <summary>
   /// A lane does not recover the journal itself (the runtime does that once, at start). If it is nevertheless offered a
   /// job the journal shows as submitting, it must not print it a second time: it leaves the job for the lane that is
   /// submitting it, or for recovery, to report. It says so in the log, once, with the job and the reason, and it ends
   /// its pass after that one claim: the server would only offer the same job again, so claiming on would spin.
   /// </summary>
   [Fact]
   public async Task LaneNeverResubmitsAJobAlreadyRecordedAsSubmitting()
   {
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.Offer(FakePrintServer.Job(Document), Document);
      journal.Record("job-1", new PrintEvent("received-before-crash", "RECEIVED"));
      journal.Record("job-1", new PrintEvent("submitting-before-crash", "SUBMITTING"));
      journal.Acknowledge("received-before-crash");
      journal.Acknowledge("submitting-before-crash");
      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(1, server.Claims);
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING" }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Empty(server.Events);
      Assert.Equal(0, server.Downloads);
      Assert.Equal(0, printer.Submissions);
      Assert.Equal("job-1: offered again while recorded as SUBMITTING, so its submission has started and not concluded; it is not submitted a second time and this pass claims nothing more.", Assert.Single(harness.Log));
   }



   /// <summary>
   /// A replay of a job another lane of this computer holds is left entirely to that lane, whatever it has recorded so
   /// far. A lane lets go of a job only after its final state was recorded and delivered, so the job in hand may be
   /// one that has just finished (the SPOOLED row); the log line is worded to be true then as well. Nothing is
   /// recorded or reported for the replay, its title and printer are not rewritten, the runtime is not told a job
   /// became active, and it is neither downloaded nor printed. The log says so once, in plain words, and the pass
   /// ends, because the server would only offer the same job again.
   /// </summary>
   [Theory]
   [InlineData("RECEIVED")]
   [InlineData("RECEIVED,SUBMITTING")]
   [InlineData("RECEIVED,SUBMITTING,SPOOLED")]
   public async Task ClaimOfAJobInHandEndsThePassRecordsNothingAndLogsOneLine(string recordedSoFar)
   {
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.Offer(FakePrintServer.Job(Document) with { Title = "Replayed title" }, Document);
      journal.TrackJob(FakePrintServer.Job(Document) with { Title = "Original title" }, "Original printer");
      foreach(string state in recordedSoFar.Split(','))
      {
         journal.Record("job-1", new PrintEvent(state + "-by-the-other-lane", state));
         journal.Acknowledge(state + "-by-the-other-lane");
      }
      string[] recordedByTheOtherLane = journal.StatesOf("job-1");
      var asked = new List<string>();
      var active = new List<string?>();
      harness.Options = harness.Options with
      {
         IsInHand = jobId =>
         {
            asked.Add(jobId);
            return true;
         },
         ActiveJobChanged = job => active.Add(job?.JobId),
      };

      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(new[] { "job-1" }, asked, StringComparer.Ordinal);
      Assert.Equal(1, server.Claims);
      Assert.Equal(recordedByTheOtherLane, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Empty(journal.Pending());
      Assert.Empty(server.Events);
      Assert.Empty(active);
      Assert.Equal("Original title", journal.Jobs["job-1"].Job.Title);
      Assert.Equal("Original printer", journal.Jobs["job-1"].PrinterName);
      Assert.Equal(0, server.Downloads);
      Assert.Equal(0, printer.Submissions);
      Assert.Equal("job-1: offered again while a lane of this computer still holds it, working on it or just finished with it; it is left to that lane, nothing is recorded for it and this pass claims nothing more.", Assert.Single(harness.Log));
   }



   /// <summary>
   /// Jobs can wait a long time for a printer's turn, and the worker does not look the printer up again when the
   /// turn is held: that would start the print helper once more for every job, while the job holds the turn and a
   /// print slot. What happened to the printer during the wait is therefore settled after SUBMITTING is recorded, by
   /// whoever can tell whether anything was sent. A printer the operator stopped sharing is refused by the exposure
   /// filter before the backend is called, and a selected media default that changed is refused by the native layer
   /// before it prints: both are FAILED. A queue that is gone is found by the native layer as well, but its failure
   /// carries no type the worker can rely on, so the job is OUTCOME_UNKNOWN like any other failure after the staged
   /// file was handed over. The fake backend plays the native layer here: it finds the queue, then matches the job's
   /// selected media against the queue's current default, and only then would print. In every case the printers are
   /// listed once (before the claim) and the job's printer is looked up once (after the download), the turn is released, nothing
   /// stays in the staging folder, and the job is neither downloaded nor printed when the server offers it again.
   /// </summary>
   [Theory]
   [InlineData("unshared", "FAILED", "The printer is not exposed to QQQ on this computer; no bytes were submitted.", 0)]
   [InlineData("media-changed", "FAILED", "The selected printer media profile changed or is unavailable. Refresh the printer and select its current default.", 1)]
   [InlineData("queue-gone", "OUTCOME_UNKNOWN", "The assigned queue was not found.", 1)]
   public async Task ChangeToThePrinterWhileWaitingForItsTurnIsSettledAfterSubmittingByWhoeverCanTell(string change, string outcome, string reason, int submissions)
   {
      PrinterMediaProfile profile = new("default-id", new string('a', 64), "Default", 101600, 152400, 300, 300);
      PrintJob job = FakePrintServer.Job(Document, options: new PrintOptions(MediaProfile: new(profile.Id, profile.Revision)));
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      printer.Printers[0] = printer.Printers[0] with { MediaProfiles = [profile] };
      printer.Printing = (submitted, _, _) =>
      {
         PrinterInfo queue = printer.Printers.FirstOrDefault(item => string.Equals(item.PrinterId, submitted.PrinterId, StringComparison.Ordinal)) ?? throw new InvalidOperationException("The assigned queue was not found.");
         PrinterMediaProfile.RequireMatch(submitted.Options, queue.MediaProfiles);
         return Task.FromResult("spool-1");
      };
      server.Offer(job, Document).Offer(job, Document);
      var steps = new List<string>();
      harness.Options = harness.Options with
      {
         AcquireSubmissionAsync = (_, _) =>
         {
            //////////////////////////////////////////////////////////////////////
            // the wait for the turn ends here; the change happened while the   //
            // job waited, after the check that follows the download had passed //
            //////////////////////////////////////////////////////////////////////
            switch(change)
            {
               case "unshared":
                  harness.Exposed.Clear();
                  break;
               case "queue-gone":
                  printer.Printers.Clear();
                  break;
               default:
                  printer.Printers[0] = printer.Printers[0] with { MediaProfiles = [profile with { Revision = new string('b', 64) }] };
                  break;
            }
            return Lease.TakeAsync(steps, "printer turn");
         },
      };

      Assert.True(await harness.RunOnceAsync());
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", outcome }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal(reason, journal.Last("job-1").Message);
      Assert.Null(journal.Last("job-1").SpoolerJobId);
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", outcome }, server.States, StringComparer.Ordinal);
      Assert.Equal(submissions, printer.Submissions);
      Assert.Equal((0, 1, 1), (printer.Discoveries, printer.Listings, printer.Lookups.Count));
      Assert.Equal(new[] { "take printer turn", "release printer turn" }, steps, StringComparer.Ordinal);
      Assert.Empty(Directory.EnumerateFileSystemEntries(harness.Staging.Path));

      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(2, server.Claims);
      Assert.Equal(1, server.Downloads);
      Assert.Equal(submissions, printer.Submissions);
      Assert.Equal(outcome, journal.GetState("job-1"));
   }



   /// <summary>A printer withdrawn before the document has been downloaded is caught by the check that follows the download: the job is FAILED with nothing submitted and never asks for the printer's turn.</summary>
   [Fact]
   public async Task PrinterWithdrawnBeforeTheDownloadIsStillFailedAndNeverTakesTheTurn()
   {
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.Offer(FakePrintServer.Job(Document), Document);
      var steps = new List<string>();
      harness.Options = harness.Options with
      {
         ReserveDocumentAsync = (_, _) =>
         {
            harness.Exposed.Clear();
            return Lease.TakeAsync(steps, "reservation");
         },
         AcquireSubmissionAsync = (_, _) => Lease.TakeAsync(steps, "printer turn"),
      };

      Assert.True(await harness.RunOnceAsync());
      Assert.Equal(new[] { "RECEIVED", "FAILED" }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal("Assigned printer or document format is unavailable.", journal.Last("job-1").Message);
      Assert.Equal(new[] { "RECEIVED", "FAILED" }, server.States, StringComparer.Ordinal);
      Assert.Equal(1, server.Downloads);
      Assert.Equal(0, printer.Submissions);
      Assert.Equal(new[] { "take reservation", "release reservation" }, steps, StringComparer.Ordinal);
      Assert.Empty(Directory.EnumerateFileSystemEntries(harness.Staging.Path));
   }



   /// <summary>
   /// The exposure filter is the check on sharing at the moment of submission, however late sharing changed: here in
   /// the instant after SUBMITTING was recorded. The filter refuses before the backend behind it is called, with a
   /// refusal only the filter can raise. The worker therefore knows nothing was sent: the job is FAILED, not unknown,
   /// its reason says that no bytes were submitted, the backend was never called, and the job is not printed when the
   /// server offers it again.
   /// </summary>
   [Fact]
   public async Task PrinterWithdrawnAfterTheSubmittingReceiptIsRefusedByTheFilterAndFailedWithNothingSent()
   {
      PrintJob job = FakePrintServer.Job(Document);
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.Offer(job, Document).Offer(job, Document);
      journal.Recorded = (_, printEvent) =>
      {
         if(string.Equals(printEvent.Status, "SUBMITTING", StringComparison.Ordinal))
         { harness.Exposed.Clear(); }
      };

      Assert.True(await harness.RunOnceAsync());
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "FAILED" }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal("The printer is not exposed to QQQ on this computer; no bytes were submitted.", journal.Last("job-1").Message);
      Assert.Null(journal.Last("job-1").SpoolerJobId);
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "FAILED" }, server.States, StringComparer.Ordinal);
      Assert.Equal(0, printer.Submissions);
      Assert.Empty(Directory.EnumerateFileSystemEntries(harness.Staging.Path));

      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(1, server.Downloads);
      Assert.Equal(0, printer.Submissions);
      Assert.Equal("FAILED", journal.GetState("job-1"));
   }



   /// <summary>
   /// Every refusal the worker makes before native submission is a plain FAILED with its reason, never the uncertain
   /// state, and the printer is never called: copies over the policy (before any download), a policy tightened while the
   /// job waited, a printer that disappeared during the download, and a pause that arrives before or just after the
   /// submitting receipt.
   /// </summary>
   [Theory]
   [InlineData("copies", "Copies exceed the effective printing policy.", 0, false)]
   [InlineData("policy-tightened", "Document or copies exceed the current submission policy.", 1, true)]
   [InlineData("printer-gone", "Assigned printer or document format is unavailable.", 1, false)]
   [InlineData("paused", "Dispatch is paused before native submission.", 1, false)]
   [InlineData("paused-after-receipt", "Dispatch stopped before native submission; no bytes were submitted.", 1, true)]
   public async Task RefusalBeforeNativeSubmissionIsFailedNotUnknown(string fault, string reason, int downloads, bool submittingRecorded)
   {
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.Offer(FakePrintServer.Job(Document, options: new PrintOptions(Copies: 3)), Document);
      int settingsReads = 0;
      int submitChecks = 0;
      var limits = new RealtimeSettings(MaxCopies: string.Equals(fault, "copies", StringComparison.Ordinal) ? 2 : 100);
      harness.Options = harness.Options with
      {
         Settings = limits,
         CurrentSettings = string.Equals(fault, "policy-tightened", StringComparison.Ordinal) ? () => new RealtimeSettings(MaxCopies: settingsReads++ == 0 ? 5 : 1) : () => limits,
         CanSubmit = fault switch
         {
            "paused" => () => false,
            "paused-after-receipt" => () => submitChecks++ == 0,
            _ => () => true,
         },
      };
      if(string.Equals(fault, "printer-gone", StringComparison.Ordinal))
      {
         printer.Finding = _ => printer.Printers.Clear();
      }
      Assert.True(await harness.RunOnceAsync());
      Assert.Equal(submittingRecorded ? new[] { "RECEIVED", "SUBMITTING", "FAILED" } : ["RECEIVED", "FAILED"], journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal(reason, journal.Last("job-1").Message);
      Assert.Equal(journal.StatesOf("job-1"), server.States, StringComparer.Ordinal);
      Assert.Equal(downloads, server.Downloads);
      Assert.Equal(0, printer.Submissions);
      Assert.Empty(Directory.EnumerateFileSystemEntries(harness.Staging.Path));
   }



   /// <summary>
   /// The path the service runs, end to end through the worker's hooks: the document is staged to disk and verified,
   /// staging space is reserved before the download and the printer's turn taken before submission, both are released
   /// in reverse order after the job is recorded, the download is paced through the shared throttle, the staged file
   /// reaches the backend with the submission limit from the live settings, and the runtime is told when the job starts
   /// and stops. Nothing is left in the staging folder, both gates end free, and the worker sent the server nothing but
   /// its claim, its document request and its three events.
   /// </summary>
   [Fact]
   public async Task StagedJobUsesEveryRuntimeHookInOrder()
   {
      var steps = new List<string>();
      int throttled = 0;
      var printer = new FakePrinterBackend
      {
         Printing = (_, path, _) =>
         {
            steps.Add("print " + (File.Exists(path) ? "staged file" : "missing file"));
            return Task.FromResult("spool-42");
         },
      };
      await using var harness = new WorkerHarness { Printer = printer };
      (FakePrintServer server, RecordingJournal journal) = (harness.Server, harness.Journal);
      server.Offer(FakePrintServer.Job(Document, format: "PDF"), Document);
      harness.Options = harness.Options with
      {
         CurrentSettings = () => new RealtimeSettings(NativeSubmissionTimeoutMillis: 45_000),
         ThrottleDownloadAsync = (bytes, _) =>
         {
            throttled += bytes;
            return Task.CompletedTask;
         },
         ReserveDocumentAsync = (_, _) => Lease.TakeAsync(steps, "reservation"),
         AcquireSubmissionAsync = (_, _) => Lease.TakeAsync(steps, "printer turn"),
         ActiveJobChanged = job => steps.Add(job is null ? "idle" : "active " + job.JobId),
      };

      Assert.True(await harness.RunOnceAsync());
      Assert.Equal(new[] { "active job-1", "take reservation", "take printer turn", "print staged file", "release printer turn", "release reservation", "idle" }, steps, StringComparer.Ordinal);
      Assert.Equal("SPOOLED", journal.GetState("job-1"));
      Assert.Equal("spool-42", journal.Last("job-1").SpoolerJobId);
      (byte[] content, TimeSpan limit) = Assert.Single(printer.Files);
      Assert.Equal(Document, content);
      Assert.Equal(TimeSpan.FromSeconds(45), limit);
      Assert.Equal(Document.Length, throttled);
      Assert.Empty(Directory.EnumerateFileSystemEntries(harness.Staging.Path));
      Assert.Equal(1, harness.EventGate.CurrentCount);
      Assert.Equal(1, harness.ClaimGate.CurrentCount);
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "SPOOLED" }, server.States, StringComparer.Ordinal);
      Assert.Equal((1, 1), (server.Claims, server.Downloads));
      Assert.Empty(server.Unexpected);
   }



   /// <summary>
   /// If the receipt for a newly claimed job cannot be delivered, nothing was submitted: the job is recorded FAILED with
   /// that explanation, the runtime is told the job is no longer active, the failure reaches the caller, and both gates
   /// are released so the next lane is not blocked.
   /// </summary>
   [Fact]
   public async Task InterruptedReceiptFailsTheJobWithoutSubmittingAndReleasesTheGates()
   {
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.Arriving = (kind, _) => string.Equals(kind, "events", StringComparison.Ordinal) ? throw new HttpRequestException("Synthetic refusal.") : Task.CompletedTask;
      server.Offer(FakePrintServer.Job(Document), Document);
      var active = new List<string?>();
      harness.Options = harness.Options with { ActiveJobChanged = job => active.Add(job?.JobId) };
      await Assert.ThrowsAsync<HttpRequestException>(() => harness.RunOnceAsync());
      Assert.Equal(new[] { "RECEIVED", "FAILED" }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal("Receipt acknowledgement was interrupted before document submission; no bytes were submitted.", journal.Last("job-1").Message);
      Assert.Equal(new[] { "job-1", null }, active, StringComparer.Ordinal);
      Assert.Equal(0, server.Downloads);
      Assert.Equal(0, printer.Submissions);
      Assert.Equal(1, harness.EventGate.CurrentCount);
      Assert.Equal(1, harness.ClaimGate.CurrentCount);
   }



   /// <summary>
   /// The lane lets go of a job whose receipt was interrupted even when the journal then fails to record FAILED for
   /// it. The runtime is still told the job is no longer active, so no ticket is left behind to make a later offer of
   /// the job read as one a lane still holds. The journal's failure is what reaches the caller, nothing was
   /// downloaded or submitted, and both gates are released.
   /// </summary>
   [Fact]
   public async Task InterruptedReceiptLetsGoOfTheJobEvenWhenRecordingItsFailureFails()
   {
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.Arriving = (kind, _) => string.Equals(kind, "events", StringComparison.Ordinal) ? throw new HttpRequestException("Synthetic refusal.") : Task.CompletedTask;
      journal.Recorded = (_, recorded) =>
      {
         if(string.Equals(recorded.Status, "FAILED", StringComparison.Ordinal))
         { throw new IOException("Synthetic journal failure."); }
      };
      server.Offer(FakePrintServer.Job(Document), Document);
      var active = new List<string?>();
      harness.Options = harness.Options with { ActiveJobChanged = job => active.Add(job?.JobId) };
      IOException failure = await Assert.ThrowsAsync<IOException>(() => harness.RunOnceAsync());
      Assert.Equal("Synthetic journal failure.", failure.Message);
      Assert.Equal(new[] { "job-1", null }, active, StringComparer.Ordinal);
      Assert.Equal(0, server.Downloads);
      Assert.Equal(0, printer.Submissions);
      Assert.Equal(1, harness.EventGate.CurrentCount);
      Assert.Equal(1, harness.ClaimGate.CurrentCount);
   }



   /// <summary>A lane told that another lane already found the queue empty does not ask the server for work at all.</summary>
   [Fact]
   public async Task LaneThatMayNotClaimSendsNoClaim()
   {
      await using var harness = new WorkerHarness();
      harness.Server.Offer(FakePrintServer.Job(Document), Document);
      harness.Options = harness.Options with { CanClaim = () => false };
      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(0, harness.Server.Claims);
   }



   /// <summary>After an outage the journal hands out undelivered events in bounded batches. The worker delivers one batch and stops without claiming while more remain, so old outcomes reach the server before new work is taken on.</summary>
   [Fact]
   public async Task OutboxIsDrainedBatchByBatchBeforeAnyClaim()
   {
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal) = (harness.Server, harness.Journal);
      server.Offer(FakePrintServer.Job(Document), Document);
      journal.BatchLimit = 2;
      foreach(int number in Enumerable.Range(1, 3))
      { journal.Record("finished-" + number, new PrintEvent("event-" + number, "SPOOLED", SpoolerJobId: "s" + number)); }
      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(2, server.Events.Count);
      Assert.Equal(0, server.Claims);
      Assert.True(await harness.RunOnceAsync());
      Assert.Equal(new[] { "finished-1", "finished-2", "finished-3", "job-1", "job-1", "job-1" }, server.Events.Select(entry => entry.JobId), StringComparer.Ordinal);
   }



   /// <summary>If the connection stops being synchronized while the printers are being listed before the claim, the worker does not claim, and sends the server nothing.</summary>
   [Fact]
   public async Task LosingSynchronizationDuringDiscoveryStopsBeforeTheClaim()
   {
      await using var harness = new WorkerHarness();
      harness.Server.Offer(FakePrintServer.Job(Document), Document);
      harness.CanSynchronize = () => harness.Printer.Listings == 0;
      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(1, harness.Printer.Listings);
      Assert.Equal(0, harness.Server.Claims);
      Assert.Empty(harness.Server.Unexpected);
   }



   /// <summary>
   /// The stop that arrives in the one place where SUBMITTING is already recorded and acknowledged but Windows has not
   /// been asked. Nothing was sent, so the job is FAILED, not unknown, and says so in words; the printer was never
   /// called. The outcome waits in the journal while the worker is stopping, is delivered by the next run, and the job
   /// is not printed when the server offers it again.
   /// </summary>
   [Fact]
   public async Task CancellationBetweenTheSubmittingReceiptAndWindowsIsFailedWithNothingSubmitted()
   {
      PrintJob job = FakePrintServer.Job(Document);
      using var stop = new CancellationTokenSource();
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.Offer(job, Document).Offer(job, Document);
      int submitChecks = 0;
      harness.Options = harness.Options with
      {
         CanSubmit = () =>
         {
            /////////////////////////////////////////////////////////////////////////
            // the second check runs after the SUBMITTING receipt was acknowledged //
            /////////////////////////////////////////////////////////////////////////
            if(++submitChecks == 2)
            { stop.Cancel(); }
            return true;
         },
      };

      Assert.True(await harness.RunOnceAsync(stop.Token));
      Assert.Equal(2, submitChecks);
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "FAILED" }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal("Stopped before native submission; no bytes were submitted.", journal.Last("job-1").Message);
      Assert.Equal(0, printer.Submissions);
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING" }, server.States, StringComparer.Ordinal);
      Assert.Equal("FAILED", Assert.Single(journal.Pending()).Event.Status);

      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "FAILED" }, server.States, StringComparer.Ordinal);
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "FAILED" }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Empty(journal.Pending());
      Assert.Equal(2, server.Claims);
      Assert.Equal(0, printer.Submissions);
   }



   /// <summary>
   /// When delivery of the SUBMITTING receipt cannot be confirmed, here because the server refuses it, Windows is never
   /// asked: the job is FAILED with nothing submitted, in those words and not the transport's, and the failure reaches
   /// the caller. Both events stay in the journal and the next run delivers them in order, each under the identifier
   /// it was recorded with, so the server can tell a replay from a new event.
   /// </summary>
   [Fact]
   public async Task UndeliveredSubmittingReceiptIsFailedWithNothingSubmittedAndReplayedUnderTheSameIds()
   {
      bool down = true;
      int eventFrames = 0;
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.Arriving = (kind, _) => string.Equals(kind, "events", StringComparison.Ordinal) && ++eventFrames > 1 && down ? throw new HttpRequestException("Synthetic refusal.") : Task.CompletedTask;
      server.Offer(FakePrintServer.Job(Document), Document);

      await Assert.ThrowsAsync<HttpRequestException>(() => harness.RunOnceAsync());
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "FAILED" }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal("Delivery of the submitting receipt could not be confirmed, so the document was not sent to the printer; no bytes were submitted.", journal.Last("job-1").Message);
      Assert.Equal(0, printer.Submissions);
      Assert.Equal(new[] { "RECEIVED" }, server.States, StringComparer.Ordinal);
      Assert.Equal(new[] { "SUBMITTING", "FAILED" }, journal.Pending().Select(entry => entry.Event.Status), StringComparer.Ordinal);

      down = false;
      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "FAILED" }, server.States, StringComparer.Ordinal);
      Assert.Equal(journal.EventIdsOf("job-1"), server.Events.Select(entry => entry.Event.EventId), StringComparer.Ordinal);
      Assert.Equal(3, journal.EventIdsOf("job-1").Distinct(StringComparer.Ordinal).Count());
      Assert.Empty(journal.Pending());
      Assert.Equal(0, printer.Submissions);
   }



   /// <summary>
   /// The same rule when the connection itself goes away while the SUBMITTING receipt waits for its acknowledgement,
   /// which is how a delivery fails in service. The server may have received the receipt, but the worker cannot know:
   /// Windows is never asked, the job is FAILED with nothing submitted, and the closed connection reaches the caller.
   /// Once the runtime has reconnected, the next pass sends on the new connection: the receipt is replayed under the
   /// identifier it was recorded with, then the outcome, and the job is still not printed.
   /// </summary>
   [Fact]
   public async Task ConnectionLostWhileTheSubmittingReceiptAwaitsItsAcknowledgementIsFailedWithNothingSubmitted()
   {
      int eventFrames = 0;
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.Arriving = (kind, _) =>
      {
         if(string.Equals(kind, "events", StringComparison.Ordinal) && ++eventFrames == 2)
         { harness.Drop(); }
         return Task.CompletedTask;
      };
      server.Offer(FakePrintServer.Job(Document), Document);

      RealtimeChannelClosedException closed = await Assert.ThrowsAsync<RealtimeChannelClosedException>(() => harness.RunOnceAsync());
      Assert.Equal("Realtime connection is closed.", closed.Message);
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "FAILED" }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal("Delivery of the submitting receipt could not be confirmed, so the document was not sent to the printer; no bytes were submitted.", journal.Last("job-1").Message);
      Assert.Equal(0, printer.Submissions);
      Assert.Equal(new[] { "SUBMITTING", "FAILED" }, journal.Pending().Select(entry => entry.Event.Status), StringComparer.Ordinal);

      harness.Reconnect();
      Assert.False(await harness.RunOnceAsync());
      string[] recorded = journal.EventIdsOf("job-1");
      Assert.Equal(new[] { recorded[0], recorded[1], recorded[1], recorded[2] }, server.Events.Select(entry => entry.Event.EventId), StringComparer.Ordinal);
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "SUBMITTING", "FAILED" }, server.States, StringComparer.Ordinal);
      Assert.Empty(journal.Pending());
      Assert.Equal(0, printer.Submissions);
   }



   /// <summary>
   /// A delivery that fails in the middle of a batch leaves the events before it acknowledged. The next run starts at
   /// the one that failed: the first event is sent once, ever, and every event arrives under its recorded identifier.
   /// </summary>
   [Fact]
   public async Task DeliveryFailureInTheMiddleOfABatchDoesNotResendWhatWasDelivered()
   {
      int eventFrames = 0;
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal) = (harness.Server, harness.Journal);
      server.Arriving = (kind, _) => string.Equals(kind, "events", StringComparison.Ordinal) && ++eventFrames == 2 ? throw new HttpRequestException("Synthetic refusal.") : Task.CompletedTask;
      foreach(int number in Enumerable.Range(1, 3))
      { journal.Record("finished-" + number, new PrintEvent("event-" + number, "SPOOLED", SpoolerJobId: "s" + number)); }

      await Assert.ThrowsAsync<HttpRequestException>(() => harness.RunOnceAsync());
      Assert.Equal(new[] { "event-1" }, server.Events.Select(entry => entry.Event.EventId), StringComparer.Ordinal);
      Assert.Equal(new[] { "event-2", "event-3" }, journal.Pending().Select(entry => entry.Event.EventId), StringComparer.Ordinal);
      Assert.Equal(0, server.Claims);

      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(new[] { "event-1", "event-2", "event-3" }, server.Events.Select(entry => entry.Event.EventId), StringComparer.Ordinal);
      Assert.Equal(4, eventFrames);
      Assert.Empty(journal.Pending());
   }



   /// <summary>
   /// The staged-file path is the one production runs, so its failures are held to the same line as its success. A
   /// failure before Windows is asked (the document does not verify, the printer went away during the download, dispatch
   /// is paused) is FAILED and the printer is never called. A refusal the native layer makes before printing (the media
   /// changed) is FAILED too. Anything else that goes wrong once the staged file was handed over (the spooler fails,
   /// the submission times out, the worker is stopped part-way) is OUTCOME_UNKNOWN. In every case the staged file is
   /// gone, each lease taken was released in reverse order, the runtime was told the job ended, and the job is neither
   /// downloaded nor printed again when the server offers it a second time.
   /// </summary>
   [Theory]
   [InlineData("document-altered", "FAILED", "Document integrity check failed.", false, 0)]
   [InlineData("printer-gone", "FAILED", "Assigned printer or document format is unavailable.", false, 0)]
   [InlineData("paused", "FAILED", "Dispatch is paused before native submission.", false, 0)]
   [InlineData("media-changed", "FAILED", "The selected media changed before native submission.", true, 1)]
   [InlineData("spooler-failed", "OUTCOME_UNKNOWN", "The spooler connection was lost.", true, 1)]
   [InlineData("timed-out", "OUTCOME_UNKNOWN", "The printer operation timed out. Inspect the printer and job outcome before retrying.", true, 1)]
   [InlineData("stopped-part-way", "OUTCOME_UNKNOWN", "The operation was canceled.", true, 1)]
   public async Task FailureOnTheStagedFilePathIsRecordedOnTheRightSideOfTheLine(string fault, string outcome, string reason, bool submittingRecorded, int submissions)
   {
      PrintJob job = FakePrintServer.Job(Document, format: "PDF");
      using var stop = new CancellationTokenSource();
      FakePrinterBackend printer = FilePrinterFailingWith(fault, reason, stop);
      await using var harness = new WorkerHarness { Printer = printer };
      (FakePrintServer server, RecordingJournal journal) = (harness.Server, harness.Journal);
      server.Offer(job, Document).Offer(job, Document);
      if(string.Equals(fault, "document-altered", StringComparison.Ordinal))
      { server.Document = [5, 4, 3, 2, 1]; }
      var steps = new List<string>();
      harness.Options = harness.Options with
      {
         CanSubmit = () => !string.Equals(fault, "paused", StringComparison.Ordinal),
         ReserveDocumentAsync = (_, _) => Lease.TakeAsync(steps, "reservation"),
         AcquireSubmissionAsync = (_, _) => Lease.TakeAsync(steps, "printer turn"),
         ActiveJobChanged = active => steps.Add(active is null ? "idle" : "active " + active.JobId),
      };

      Assert.True(await harness.RunOnceAsync(stop.Token));
      Assert.Equal(submittingRecorded ? new[] { "RECEIVED", "SUBMITTING", outcome } : ["RECEIVED", outcome], journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal(reason, journal.Last("job-1").Message);
      Assert.Null(journal.Last("job-1").SpoolerJobId);
      Assert.Equal(submissions, printer.Submissions);
      Assert.Empty(Directory.EnumerateFileSystemEntries(harness.Staging.Path));
      string[] printerTurn = submittingRecorded || string.Equals(fault, "paused", StringComparison.Ordinal) ? ["take printer turn", "release printer turn"] : [];
      string[] expectedSteps = ["active job-1", "take reservation", .. printerTurn, "release reservation", "idle"];
      Assert.Equal(expectedSteps, steps, StringComparer.Ordinal);

      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(journal.StatesOf("job-1"), server.States, StringComparer.Ordinal);
      Assert.Empty(journal.Pending());
      Assert.Equal(2, server.Claims);
      Assert.Equal(1, server.Downloads);
      Assert.Equal(submissions, printer.Submissions);
      Assert.Equal(outcome, journal.GetState("job-1"));
   }



   /// <summary>
   /// A lease whose release fails cannot change what is known about the print. Once the job is recorded as SPOOLED,
   /// Windows has accepted it: the record stays SPOOLED with its spooler receipt, nothing is written over it, the
   /// failed release is logged, and the lane counts the job as spooled. When the release fails after a submission that
   /// had already failed, the job is OUTCOME_UNKNOWN once, as it would have been; and when it fails before Windows was
   /// asked, the job is FAILED once.
   /// </summary>
   [Theory]
   [InlineData("printer turn", "spooled", "SPOOLED")]
   [InlineData("reservation", "spooled", "SPOOLED")]
   [InlineData("printer turn", "spooler-failed", "OUTCOME_UNKNOWN")]
   [InlineData("reservation", "document-altered", "FAILED")]
   public async Task LeaseThatFailsToReleaseNeverChangesWhatIsKnownAboutThePrint(string failingLease, string fault, string outcome)
   {
      var printer = new FakePrinterBackend
      {
         Printing = (_, _, _) => string.Equals(fault, "spooler-failed", StringComparison.Ordinal) ? throw new IOException("The spooler connection was lost.") : Task.FromResult("spool-42"),
      };
      await using var harness = new WorkerHarness { Printer = printer };
      (FakePrintServer server, RecordingJournal journal) = (harness.Server, harness.Journal);
      server.Offer(FakePrintServer.Job(Document, format: "PDF"), Document);
      if(string.Equals(fault, "document-altered", StringComparison.Ordinal))
      { server.Document = [5, 4, 3, 2, 1]; }
      var steps = new List<string>();
      harness.Options = harness.Options with
      {
         ReserveDocumentAsync = (_, _) => Lease.TakeAsync(steps, "reservation", fails: string.Equals(failingLease, "reservation", StringComparison.Ordinal)),
         AcquireSubmissionAsync = (_, _) => Lease.TakeAsync(steps, "printer turn", fails: string.Equals(failingLease, "printer turn", StringComparison.Ordinal)),
      };

      Assert.True(await harness.RunOnceAsync());
      bool spooled = string.Equals(outcome, "SPOOLED", StringComparison.Ordinal);
      string[] states = string.Equals(outcome, "FAILED", StringComparison.Ordinal) ? ["RECEIVED", "FAILED"] : ["RECEIVED", "SUBMITTING", outcome];
      Assert.Equal(states, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal(states, server.States, StringComparer.Ordinal);
      Assert.Equal(spooled ? "spool-42" : null, journal.Last("job-1").SpoolerJobId);
      Assert.Empty(journal.Pending());
      Assert.Contains("release " + failingLease, steps, StringComparer.Ordinal);
      Assert.Empty(Directory.EnumerateFileSystemEntries(harness.Staging.Path));
      Assert.Equal(spooled ? 1 : 0, harness.Log.Count(line => string.Equals(line, "job-1: spooled, and stays recorded as spooled; releasing what the job held failed afterwards: Synthetic release failure.", StringComparison.Ordinal)));
   }



   /// <summary>
   /// A file-taking printer for one fault of the staged path: its queue disappears at the lookup after the download, or its
   /// submission is refused for changed media, fails, times out or is cancelled part-way by stopping the worker. For
   /// the faults that happen before submission it is an ordinary printer that is never reached.
   /// </summary>
   private static FakePrinterBackend FilePrinterFailingWith(string fault, string reason, CancellationTokenSource stop)
   {
      var printer = new FakePrinterBackend
      {
         Printing = async (_, _, token) =>
         {
            switch(fault)
            {
               case "media-changed":
                  throw new PrinterMediaMismatchException(reason);
               case "spooler-failed":
                  throw new IOException(reason);
               case "timed-out":
                  throw new TimeoutException(reason);
               default:
                  await stop.CancelAsync();
                  token.ThrowIfCancellationRequested();
                  return "never-returned";
            }
         },
      };
      if(string.Equals(fault, "printer-gone", StringComparison.Ordinal))
      {
         printer.Finding = _ => printer.Printers.Clear();
      }
      return printer;
   }



   /// <summary>A lease handed out by a runtime hook; it records when it was taken and when it was released, and its release can be made to fail.</summary>
   private sealed class Lease(List<string> steps, string name, bool fails) : IAsyncDisposable
   {
      /// <summary>Records the lease being taken and returns it.</summary>
      internal static Task<IAsyncDisposable> TakeAsync(List<string> steps, string name, bool fails = false)
      {
         steps.Add("take " + name);
         return Task.FromResult<IAsyncDisposable>(new Lease(steps, name, fails));
      }



      /// <summary>Records the lease being released, then fails when the test asked for a release that fails.</summary>
      public ValueTask DisposeAsync()
      {
         steps.Add("release " + name);
         return fails ? ValueTask.FromException(new InvalidOperationException("Synthetic release failure.")) : ValueTask.CompletedTask;
      }
   }
}
