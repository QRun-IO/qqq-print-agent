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
      (FakePrintServer server, RecordingJournal journal, FakeFilePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.Offer(job, Document).Offer(job, Document);
      printer.PrintingFile = (_, _, _) => throw (timedOut ? new TimeoutException(reason) : new IOException(reason));

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
      (FakePrintServer server, RecordingJournal journal, FakeFilePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.Offer(job, Document).Offer(job, Document);
      printer.PrintingFile = async (_, _, token) =>
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
      (FakePrintServer server, RecordingJournal journal, FakeFilePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
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
      (FakePrintServer server, RecordingJournal journal, FakeFilePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
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
         printer.Discovering = () =>
         {
            if(printer.Discoveries > 1)
            { printer.Printers.Clear(); }
         };
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
      var printer = new FakeFilePrinterBackend
      {
         PrintingFile = (_, path, _) =>
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
      Assert.Empty(printer.Printed);
      Assert.Equal(Document.Length, throttled);
      Assert.Empty(Directory.EnumerateFileSystemEntries(harness.Staging.Path));
      Assert.Equal(1, harness.EventGate.CurrentCount);
      Assert.Equal(1, harness.ClaimGate.CurrentCount);
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "SPOOLED" }, server.States, StringComparer.Ordinal);
      Assert.Equal((1, 1), (server.Claims, server.Downloads));
      Assert.Empty(server.Unexpected);
   }



   /// <summary>
   /// A worker told to stage documents to files hands staged files to its backend, so one composed with a backend that
   /// only takes in-memory content is refused when it is built, naming the backend parameter, before it can claim
   /// anything. The same backend is fine for a worker that stages nothing, and a file backend is fine for either.
   /// </summary>
   [Fact]
   public void WorkerThatStagesFilesIsRefusedAtConstructionUnlessItsBackendTakesFiles()
   {
      using var staging = new TempDirectory();
      using var server = new FakePrintServer();
      using PrintApiClient api = Api(server);
      var journal = new RecordingJournal();
      var staged = new PrintWorkerOptions { StagingDirectory = staging.Path };
      ArgumentException refused = Assert.Throws<ArgumentException>(() => new PrintWorker(api, new FakePrinterBackend(), journal, options: staged));
      Assert.Equal("printers", refused.ParamName);
      Assert.StartsWith("A worker that stages documents to files needs a printer backend that takes files.", refused.Message, StringComparison.Ordinal);
      Assert.NotNull(new PrintWorker(api, new FakeFilePrinterBackend(), journal, options: staged));
      Assert.NotNull(new PrintWorker(api, new FakePrinterBackend(), journal, options: new PrintWorkerOptions()));
      Assert.NotNull(new PrintWorker(api, new FakePrinterBackend(), journal));
      Assert.NotNull(new PrintWorker(api, new FakeFilePrinterBackend(), journal));
      Assert.Equal(0, server.Claims);
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
      (FakePrintServer server, RecordingJournal journal, FakeFilePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
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



   /// <summary>If the connection stops being synchronized while printers are being discovered, the worker does not claim, and sends the server nothing.</summary>
   [Fact]
   public async Task LosingSynchronizationDuringDiscoveryStopsBeforeTheClaim()
   {
      await using var harness = new WorkerHarness();
      harness.Server.Offer(FakePrintServer.Job(Document), Document);
      harness.CanSynchronize = () => harness.Printer.Discoveries == 0;
      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(1, harness.Printer.Discoveries);
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
      (FakePrintServer server, RecordingJournal journal, FakeFilePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
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
      (FakePrintServer server, RecordingJournal journal, FakeFilePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
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
      (FakePrintServer server, RecordingJournal journal, FakeFilePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
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
      FakeFilePrinterBackend printer = FilePrinterFailingWith(fault, reason, stop);
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
      Assert.Empty(printer.Printed);
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
      var printer = new FakeFilePrinterBackend
      {
         PrintingFile = (_, _, _) => string.Equals(fault, "spooler-failed", StringComparison.Ordinal) ? throw new IOException("The spooler connection was lost.") : Task.FromResult("spool-42"),
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
   /// A file-taking printer for one fault of the staged path: its queue disappears after the first discovery, or its
   /// submission is refused for changed media, fails, times out or is cancelled part-way by stopping the worker. For
   /// the faults that happen before submission it is an ordinary printer that is never reached.
   /// </summary>
   private static FakeFilePrinterBackend FilePrinterFailingWith(string fault, string reason, CancellationTokenSource stop)
   {
      var printer = new FakeFilePrinterBackend
      {
         PrintingFile = async (_, _, token) =>
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
         printer.Discovering = () =>
         {
            if(printer.Discoveries > 1)
            { printer.Printers.Clear(); }
         };
      }
      return printer;
   }



   /// <summary>A device-token API client over the fake server; nothing leaves the process.</summary>
   private static PrintApiClient Api(FakePrintServer server) => new("http://localhost", "synthetic-device-token", server);



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
