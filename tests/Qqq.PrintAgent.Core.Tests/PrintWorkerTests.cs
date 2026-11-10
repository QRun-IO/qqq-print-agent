/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// The print worker is what stands between a claimed job and the spooler. These tests guard the duplicate-print boundary (a job
/// whose outcome is uncertain is never sent again), content verification, media drift, receipts that are drained even when
/// dispatch is paused, and the timing line that explains slow prints.
/// </summary>
public sealed class PrintWorkerTests
{
   private static readonly byte[] Document = Encoding.UTF8.GetBytes("^XA^FO10,10^FDQQQ test^FS^XZ");



   /// <summary>
   /// If the printer's selected media changes between claiming and submission, either as seen by inventory or by the native
   /// layer, the job fails without a spooler receipt, and a refusal the native layer made before printing is reported as
   /// FAILED rather than the uncertain state. An unchanged printer spools exactly once. Inventory drift stops before
   /// SUBMITTING is recorded, so the native layer is never reached.
   /// </summary>
   [Theory]
   [InlineData(true, false, "FAILED", 0)]
   [InlineData(false, true, "FAILED", 1)]
   [InlineData(false, false, "SPOOLED", 1)]
   public async Task SelectedMediaDriftCannotSpoolAndKnownNativeRefusalIsNotUncertain(bool discoveryDrift, bool nativeDrift, string expected, int reachedNative)
   {
      PrinterMediaProfile profile = new("default-id", new string('a', 64), "Default", 101600, 152400, 300, 300);
      await using var harness = new WorkerHarness();
      FakeFilePrinterBackend printer = harness.Printer;
      printer.Printers[0] = printer.Printers[0] with { MediaProfiles = [profile] };
      printer.Discovering = () =>
      {
         if(discoveryDrift && printer.Discoveries > 1)
         { printer.Printers[0] = printer.Printers[0] with { MediaProfiles = [profile with { Revision = new string('b', 64) }] }; }
      };
      if(nativeDrift)
      { printer.PrintingFile = (_, _, _) => throw new PrinterMediaMismatchException("Fixture default changed before native submission."); }
      harness.Server.Offer(FakePrintServer.Job(Document, options: new PrintOptions(MediaProfile: new(profile.Id, profile.Revision))), Document);

      Assert.True(await harness.RunOnceAsync());
      Assert.Equal(reachedNative, printer.Submissions);
      Assert.Equal(expected, harness.Journal.GetState("job-1"));
      bool spooled = string.Equals(expected, "SPOOLED", StringComparison.Ordinal);
      Assert.Equal(spooled ? "spool-file-1" : null, harness.Journal.Last("job-1").SpoolerJobId);
      Assert.Equal(discoveryDrift ? ["RECEIVED", "FAILED"] : new[] { "RECEIVED", "SUBMITTING", expected }, harness.Server.States, StringComparer.Ordinal);
   }



   /// <summary>
   /// While dispatch is paused, whether or not the connection is synchronized, the worker still delivers any pending
   /// receipt. It must not claim, so the server never hands out work to a device that has been told to stop, and it
   /// sends nothing else: inventory and heartbeats are the runtime's to send.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task PausedOrDisconnectedDrainsReceiptsWithoutClaiming(bool synchronize)
   {
      await using var harness = new WorkerHarness { CanDispatch = () => false, CanSynchronize = () => synchronize };
      harness.Server.Offer(FakePrintServer.Job(Document), Document);
      harness.Journal.Record("finished", new PrintEvent("receipt", "SPOOLED", SpoolerJobId: "1"));
      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(0, harness.Server.Claims);
      Assert.Empty(harness.Journal.Pending());
      Assert.Equal("SPOOLED", Assert.Single(harness.Server.States));
      Assert.Empty(harness.Server.Unexpected);
      Assert.Equal(0, harness.Printer.Submissions);
   }



   /// <summary>A server that offers a job the journal already holds in a terminal state (spooled, completed, failed or unknown) must not cause another download or print and must not generate new receipts; this is the protection against duplicate output after a redelivery.</summary>
   [Theory]
   [InlineData("SPOOLED")]
   [InlineData("SPOOLER_COMPLETED")]
   [InlineData("FAILED")]
   [InlineData("OUTCOME_UNKNOWN")]
   public async Task RunOnce_RetainedTerminalStateNeverResubmits(string state)
   {
      await using var harness = new WorkerHarness();
      harness.Server.Offer(FakePrintServer.Job(Document), Document);
      harness.Journal.Record("job-1", new PrintEvent("previous", state));
      harness.Journal.Acknowledge("previous");
      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(1, harness.Server.Claims);
      Assert.Equal(0, harness.Server.Downloads);
      Assert.Equal(0, harness.Printer.Submissions);
      Assert.Empty(harness.Server.Events);
      Assert.Equal(new[] { state }, harness.Journal.StatesOf("job-1"), StringComparer.Ordinal);
   }



   /// <summary>The normal path: a job whose bytes match its hash is staged, printed once from the staged file and receipted RECEIVED, SUBMITTING, SPOOLED in order; the next pass finds nothing, the journal keeps the job and printer name, and nothing is left in the staging folder.</summary>
   [Fact]
   public async Task RunOnce_SubmitsVerifiedContentAndDoesNotDuplicate()
   {
      PrintJob job = FakePrintServer.Job(Document);
      await using var harness = new WorkerHarness();
      harness.Server.Offer(job, Document);
      Assert.True(await harness.RunOnceAsync());
      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(1, harness.Printer.Submissions);
      Assert.Equal(Document, Assert.Single(harness.Printer.Files).Content);
      Assert.Equal("SPOOLED", harness.Journal.GetState("job-1"));
      Assert.Equal(job, harness.Journal.Jobs["job-1"].Job);
      Assert.Equal("Queue one", harness.Journal.Jobs["job-1"].PrinterName);
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "SPOOLED" }, harness.Server.States, StringComparer.Ordinal);
      Assert.Empty(harness.Server.Unexpected);
      Assert.Empty(Directory.EnumerateFileSystemEntries(harness.Staging.Path));
   }



   /// <summary>
   /// Proves each processed job logs exactly one phase-timing line naming every phase and only the job identifier, so a slow print can be
   /// attributed from the service log without exposing document content or adding any request.
   /// </summary>
   [Fact]
   public async Task RunOnce_LogsOnePhaseTimingLinePerJobWithoutDocumentContent()
   {
      byte[] content = Encoding.UTF8.GetBytes("^XA^FO10,10^FDprivate label text^FS^XZ");
      await using var harness = new WorkerHarness();
      harness.Server.Offer(FakePrintServer.Job(content, "timing-job"), content);
      Assert.True(await harness.RunOnceAsync());
      string timing = Assert.Single(harness.Log, line => line.Contains(": timing ", StringComparison.Ordinal));
      Assert.StartsWith("timing-job: timing ", timing, StringComparison.Ordinal);
      foreach(string phase in new[] { "inventory=", "claim=", "download=", "revalidate=", "gate=", "submit=" })
      {
         Assert.Contains(phase, timing, StringComparison.Ordinal);
      }
      Assert.DoesNotContain(harness.Log, line => line.Contains("private label text", StringComparison.Ordinal));
   }



   /// <summary>The receipt records the operator's friendly printer name when one is set and falls back to the queue name otherwise, while the job's printer id used for dispatch stays unchanged.</summary>
   [Theory]
   [InlineData("Dispatch labels", "Dispatch labels")]
   [InlineData("", "Queue one")]
   [InlineData(null, "Queue one")]
   public async Task ReceiptCapturesFriendlyNameWithoutChangingDispatchIdentity(string? displayName, string expected)
   {
      await using var harness = new WorkerHarness();
      harness.Printer.Printers[0] = harness.Printer.Printers[0] with { DisplayName = displayName };
      harness.Server.Offer(FakePrintServer.Job(Document, "named-job"), Document);
      Assert.True(await harness.RunOnceAsync());
      Assert.Equal(expected, harness.Journal.Jobs["named-job"].PrinterName);
      Assert.Equal("printer1", harness.Journal.Jobs["named-job"].Job.PrinterId);
      Assert.Equal(1, harness.Printer.Submissions);
   }



   /// <summary>If the journal shows a previous attempt got as far as SUBMITTING, the worker cannot know whether paper came out, so it records OUTCOME_UNKNOWN and does not print again.</summary>
   [Fact]
   public async Task RunOnce_PriorSubmittingMarksUnknownWithoutPrinting()
   {
      PrintJob job = new("job1", "printer1", "Test", "ZPL", new string('0', 64), 1, new PrintOptions(), "", "CLAIMED");
      using FixtureHandler handler = new(job, [1]);
      using PrintApiClient api = new("http://localhost", "test-device", handler);
      MemoryJournal journal = new();
      journal.Record("job1", new PrintEvent("old", "SUBMITTING"));
      journal.Acknowledge("old");
      FakePrinter printer = new();
      await new PrintWorker(api, printer, journal).RunOnceAsync();
      Assert.Equal(0, printer.PrintCount);
      Assert.Equal("OUTCOME_UNKNOWN", journal.GetState("job1"));
   }



   /// <summary>
   /// After a restart, an acknowledged job stuck at RECEIVED is closed as FAILED (nothing was submitted) and one stuck at
   /// SUBMITTING as OUTCOME_UNKNOWN (it may have printed). The conclusion is reported once, with no new claim.
   /// </summary>
   [Theory]
   [InlineData("RECEIVED", "FAILED")]
   [InlineData("SUBMITTING", "OUTCOME_UNKNOWN")]
   public async Task RunOnce_RecoversAcknowledgedInterruptedStateWithoutClaim(string previous, string expected)
   {
      using FixtureHandler handler = new(null, []);
      using PrintApiClient api = new("http://localhost", "test-device", handler);
      MemoryJournal journal = new();
      journal.Record("interrupted", new PrintEvent("before-stop", previous));
      journal.Acknowledge("before-stop");
      FakePrinter printer = new();
      PrintWorker worker = new(api, printer, journal);
      await worker.RunOnceAsync();
      await worker.RunOnceAsync();
      Assert.Equal(0, printer.PrintCount);
      Assert.Equal(expected, journal.GetState("interrupted"));
      Assert.Equal(new[] { expected }, handler.Events, StringComparer.Ordinal);
      Assert.Empty(journal.Pending());
   }



   /// <summary>Receipts the server never acknowledged are resent in their original order before the new OUTCOME_UNKNOWN conclusion, so the server's history stays in the sequence things actually happened.</summary>
   [Fact]
   public async Task RunOnce_RecoversUnacknowledgedSubmissionInOriginalEventOrder()
   {
      using FixtureHandler handler = new(null, []);
      using PrintApiClient api = new("http://localhost", "test-device", handler);
      MemoryJournal journal = new();
      journal.Record("interrupted", new PrintEvent("received", "RECEIVED"));
      journal.Record("interrupted", new PrintEvent("submitting", "SUBMITTING"));
      FakePrinter printer = new();
      await new PrintWorker(api, printer, journal).RunOnceAsync();
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "OUTCOME_UNKNOWN" }, handler.Events, StringComparer.Ordinal);
      Assert.Equal(0, printer.PrintCount);
   }



   /// <summary>Content whose hash does not match the claim is never sent to the printer; the job is marked FAILED instead, and the partial file is gone from the staging folder.</summary>
   [Fact]
   public async Task RunOnce_BadHashNeverSubmits()
   {
      await using var harness = new WorkerHarness();
      harness.Server.Offer(FakePrintServer.Job(Document) with { ContentSha256 = new string('0', 64) }, Document);
      Assert.True(await harness.RunOnceAsync());
      Assert.Equal(1, harness.Server.Downloads);
      Assert.Equal(0, harness.Printer.Submissions);
      Assert.Equal("FAILED", harness.Journal.GetState("job-1"));
      Assert.Empty(Directory.EnumerateFileSystemEntries(harness.Staging.Path));
   }



   /// <summary>
   /// Events a previous run recorded and never delivered go out first, in the order they were recorded, before the
   /// worker asks for work: the two receipts of an interrupted job and the outcome the runtime's recovery recorded for
   /// it reach the server as RECEIVED, SUBMITTING, OUTCOME_UNKNOWN, so its history stays in the sequence things
   /// happened. The job is not printed, and the claim that follows finds all three already delivered.
   /// </summary>
   [Fact]
   public async Task EventsLeftFromAPreviousRunAreDeliveredInRecordedOrderBeforeAnyClaim()
   {
      await using var harness = new WorkerHarness();
      harness.Journal.Record("interrupted", new PrintEvent("received", "RECEIVED"));
      harness.Journal.Record("interrupted", new PrintEvent("submitting", "SUBMITTING"));
      harness.Journal.Record("interrupted", new PrintEvent("recovered", "OUTCOME_UNKNOWN", "Agent stopped during native submission; inspect the printer before retrying."));
      int deliveredBeforeTheClaim = -1;
      harness.Server.Arriving = (kind, _) =>
      {
         if(string.Equals(kind, "claim", StringComparison.Ordinal))
         { deliveredBeforeTheClaim = harness.Server.Events.Count; }
         return Task.CompletedTask;
      };
      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(new[] { "received", "submitting", "recovered" }, harness.Server.Events.Select(entry => entry.Event.EventId), StringComparer.Ordinal);
      Assert.Equal(3, deliveredBeforeTheClaim);
      Assert.Empty(harness.Journal.Pending());
      Assert.Equal(0, harness.Printer.Submissions);
   }



   /// <summary>The API client refuses a plain http address for a remote host, so a device token cannot be sent unencrypted.</summary>
   [Fact]
   public void Client_RejectsPlaintextRemoteServer()
   {
      Assert.Throws<ArgumentException>(() => new PrintApiClient("http://example.com"));
   }



   /// <summary>Fake server: requires bearer authentication, offers the single configured job once per claim, serves its bytes, and records the status of every event it receives.</summary>
   private sealed class FixtureHandler(PrintJob? job, byte[] content) : HttpMessageHandler
   {
      internal int Claims { get; private set; }
      internal int Heartbeats { get; private set; }
      internal int Inventories { get; private set; }
      public List<string> Events { get; } = [];



      /// <summary>Counts claims, heartbeats and inventories by URL suffix, returns the content or an event acknowledgement, and answers a claim with the configured job.</summary>
      protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
         string path = request.RequestUri!.AbsolutePath;
         if(path.EndsWith("/claim", StringComparison.Ordinal))
         { Claims++; }
         if(path.EndsWith("/heartbeat", StringComparison.Ordinal))
         { Heartbeats++; }
         if(path.EndsWith("/printers", StringComparison.Ordinal))
         { Inventories++; }
         if(path.EndsWith("/content", StringComparison.Ordinal))
         {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
         }
         if(path.EndsWith("/events", StringComparison.Ordinal))
         {
            PrintEvent printEvent = JsonSerializer.Deserialize<PrintEvent>(await request.Content!.ReadAsStringAsync(cancellationToken), PrintApiClient.JsonOptions)!;
            Events.Add(printEvent.Status);
         }
         string json = path.EndsWith("/claim", StringComparison.Ordinal) ? JsonSerializer.Serialize(new JobClaim(job), PrintApiClient.JsonOptions) : "{}";
         return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
      }
   }



   /// <summary>
   /// A single ZPL queue that counts prints. It can change its media profile revision after the first inventory (discoveryDrift)
   /// or refuse at print time with a media mismatch (nativeDrift), reproducing the two places selected media can drift.
   /// </summary>
   private sealed class FakePrinter(string? displayName = null, PrinterMediaProfile? profile = null, bool discoveryDrift = false, bool nativeDrift = false) : IPrinterBackend
   {
      private int _discoveries;
      public int PrintCount { get; private set; }



      /// <summary>Returns the queue, with its media profile revision altered from the second call onward when drift is enabled.</summary>
      public IReadOnlyList<PrinterInfo> Discover()
      {
         PrinterMediaProfile? current = profile;
         if(discoveryDrift && _discoveries++ > 0 && current is not null)
         { current = current with { Revision = new string('b', 64) }; }
         return [new("printer1", "Test", "Zebra", "USB", "READY", ["ZPL"], DisplayName: displayName, MediaProfiles: current is null ? null : [current])];
      }



      /// <summary>Throws a media-mismatch refusal when native drift is enabled; otherwise counts the print and returns spooler id 123.</summary>
      public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
      {
         if(nativeDrift)
         { throw new PrinterMediaMismatchException("Fixture default changed before native submission."); }
         PrintCount++;
         return Task.FromResult("123");
      }
   }



   /// <summary>In-memory stand-in for the durable job journal, with the same recording, acknowledgement and incomplete-job semantics the worker relies on.</summary>
   private sealed class MemoryJournal : IJobJournal
   {
      public Dictionary<string, (PrintJob Job, string? PrinterName)> Jobs { get; } = new(StringComparer.Ordinal);



      /// <summary>Remembers the job and the printer name shown for it.</summary>
      public void TrackJob(PrintJob job, string? printerName) => Jobs[job.JobId] = (job, printerName);



      private readonly List<(string JobId, PrintEvent Event)> _events = [];
      private readonly HashSet<string> _acknowledged = new(StringComparer.Ordinal);



      /// <summary>The status of the most recent event recorded for the job, or null if none.</summary>
      public string? GetState(string jobId) => _events.LastOrDefault(e => string.Equals(e.JobId, jobId, StringComparison.Ordinal)).Event?.Status;



      /// <summary>Appends an event, unacknowledged until the server confirms it.</summary>
      public void Record(string jobId, PrintEvent printEvent) => _events.Add((jobId, printEvent));



      /// <summary>Events the server has not yet acknowledged, in the order they were recorded.</summary>
      public IReadOnlyList<(string JobId, PrintEvent Event)> Pending() => _events.Where(e => !_acknowledged.Contains(e.Event.EventId)).ToList();



      /// <summary>Marks the event as delivered so it is no longer pending.</summary>
      public void Acknowledge(string eventId) => _acknowledged.Add(eventId);



      /// <summary>Jobs whose latest event is RECEIVED or SUBMITTING, meaning the agent stopped before finishing them.</summary>
      public IReadOnlyList<(string JobId, string State)> Incomplete() => _events.GroupBy(e => e.JobId, StringComparer.Ordinal).Select(group => group.Last()).Where(e => e.Event.Status is "RECEIVED" or "SUBMITTING").Select(e => (e.JobId, e.Event.Status)).ToArray();
   }
}

