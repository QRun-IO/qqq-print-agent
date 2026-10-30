using System.Net;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

public sealed class PrintWorkerTests
{
   [Theory]
   [InlineData("SPOOLED")]
   [InlineData("SPOOLER_COMPLETED")]
   [InlineData("FAILED")]
   [InlineData("OUTCOME_UNKNOWN")]
   public async Task RunOnce_RetainedTerminalStateNeverResubmits(string state)
   {
      PrintJob job = new("job1", "printer1", "Test", "ZPL", "00", 1, new PrintOptions(), "", "CLAIMED");
      using FixtureHandler handler = new(job, [1]);
      using PrintApiClient api = new("http://localhost", "test-device", handler);
      MemoryJournal journal = new();
      journal.Record("job1", new PrintEvent("previous", state));
      journal.Acknowledge("previous");
      FakePrinter printer = new();
      await new PrintWorker(api, printer, journal).RunOnceAsync();
      Assert.Equal(0, printer.PrintCount);
      Assert.Empty(handler.Events);
      Assert.Equal(state, journal.GetState("job1"));
   }

   [Fact]
   public async Task RunOnce_SubmitsVerifiedContentAndDoesNotDuplicate()
   {
      byte[] content = Encoding.UTF8.GetBytes("^XA^FO10,10^FDQQQ test^FS^XZ");
      PrintJob job = new("job1", "printer1", "Test", "ZPL", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)), content.Length, new PrintOptions(), "2026-10-29T00:00:00Z", "CLAIMED");
      using FixtureHandler handler = new(job, content);
      using PrintApiClient api = new("http://localhost", "test-device", handler);
      MemoryJournal journal = new();
      FakePrinter printer = new();
      PrintWorker worker = new(api, printer, journal);
      await worker.RunOnceAsync();
      await worker.RunOnceAsync();
      Assert.Equal(1, printer.PrintCount);
      Assert.Equal("SPOOLED", journal.GetState("job1"));
      Assert.Equal(job, journal.Jobs["job1"].Job);
      Assert.Equal("Test", journal.Jobs["job1"].PrinterName);
      Assert.Equal(new[] { "RECEIVED", "SUBMITTING", "SPOOLED" }, handler.Events, StringComparer.Ordinal);
   }

   [Theory]
   [InlineData("Dispatch labels", "Dispatch labels")]
   [InlineData("", "Test")]
   [InlineData(null, "Test")]
   public async Task ReceiptCapturesFriendlyNameWithoutChangingDispatchIdentity(string? displayName, string expected)
   {
      byte[] content = [1];
      PrintJob job = new("named-job", "printer1", "Test", "ZPL", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)), 1, new PrintOptions(), "2026-01-01T00:00:00Z", "CLAIMED");
      using FixtureHandler handler = new(job, content);
      using PrintApiClient api = new("http://localhost", "test-device", handler);
      MemoryJournal journal = new();
      FakePrinter printer = new(displayName);
      await new PrintWorker(api, printer, journal).RunOnceAsync();
      Assert.Equal(expected, journal.Jobs[job.JobId].PrinterName);
      Assert.Equal("printer1", journal.Jobs[job.JobId].Job.PrinterId);
      Assert.Equal(1, printer.PrintCount);
   }

   [Fact]
   public async Task RunOnce_PriorSubmittingMarksUnknownWithoutPrinting()
   {
      PrintJob job = new("job1", "printer1", "Test", "ZPL", "00", 1, new PrintOptions(), "", "CLAIMED");
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

   [Fact]
   public async Task RunOnce_BadHashNeverSubmits()
   {
      PrintJob job = new("job1", "printer1", "Test", "ZPL", "00", 1, new PrintOptions(), "", "CLAIMED");
      using FixtureHandler handler = new(job, [1]);
      using PrintApiClient api = new("http://localhost", "test-device", handler);
      MemoryJournal journal = new();
      FakePrinter printer = new();
      await new PrintWorker(api, printer, journal).RunOnceAsync();
      Assert.Equal(0, printer.PrintCount);
      Assert.Equal("FAILED", journal.GetState("job1"));
   }

   [Fact]
   public void Client_RejectsPlaintextRemoteServer()
   {
      Assert.Throws<ArgumentException>(() => new PrintApiClient("http://example.com"));
   }

   private sealed class FixtureHandler(PrintJob? job, byte[] content) : HttpMessageHandler
   {
      public List<string> Events { get; } = [];
      protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
         string path = request.RequestUri!.AbsolutePath;
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

   private sealed class FakePrinter(string? displayName = null) : IPrinterBackend
   {
      public int PrintCount { get; private set; }
      public IReadOnlyList<PrinterInfo> Discover() => [new("printer1", "Test", "Zebra", "USB", "READY", ["ZPL"], DisplayName: displayName)];
      public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken) { PrintCount++; return Task.FromResult("123"); }
   }

   private sealed class MemoryJournal : IJobJournal
   {
      public Dictionary<string, (PrintJob Job, string? PrinterName)> Jobs { get; } = new(StringComparer.Ordinal);
      public void TrackJob(PrintJob job, string? printerName) => Jobs[job.JobId] = (job, printerName);
      private readonly List<(string JobId, PrintEvent Event)> _events = [];
      private readonly HashSet<string> _acknowledged = new(StringComparer.Ordinal);
      public string? GetState(string jobId) => _events.LastOrDefault(e => string.Equals(e.JobId, jobId, StringComparison.Ordinal)).Event?.Status;
      public void Record(string jobId, PrintEvent printEvent) => _events.Add((jobId, printEvent));
      public IReadOnlyList<(string JobId, PrintEvent Event)> Pending() => _events.Where(e => !_acknowledged.Contains(e.Event.EventId)).ToList();
      public void Acknowledge(string eventId) => _acknowledged.Add(eventId);
      public IReadOnlyList<(string JobId, string State)> Incomplete() => _events.GroupBy(e => e.JobId, StringComparer.Ordinal).Select(group => group.Last()).Where(e => e.Event.Status is "RECEIVED" or "SUBMITTING").Select(e => (e.JobId, e.Event.Status)).ToArray();
   }
}

