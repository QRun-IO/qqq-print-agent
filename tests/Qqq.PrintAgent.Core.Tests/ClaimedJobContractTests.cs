/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// Jobs are untrusted input: the server names what to print and where. These tests feed the API client every malformed
/// shape of a claim and of the queued-job list and require one protocol-violation failure for each, then check what the
/// worker does with a refused job: it reports it as FAILED so the queue behind it is not blocked, and it never
/// downloads or prints anything for it.
/// </summary>
public sealed class ClaimedJobContractTests
{
   private static readonly string Hash = new('a', 64);



   /// <summary>The shared claim fixtures are accepted: the sample claim yields its job, and the empty claim (and a claim with no job member at all) yields none.</summary>
   [Fact]
   public async Task SharedClaimFixturesAreAccepted()
   {
      using var server = new FakePrintServer();
      server.OfferRaw(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "claim.response.json")))
         .OfferRaw(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "claim-empty.response.json")))
         .OfferRaw("{}");
      using PrintApiClient api = Api(server);
      PrintJob job = Assert.IsType<PrintJob>(await api.ClaimAsync(CancellationToken.None));
      Assert.Equal("job-example", job.JobId);
      Assert.Equal("zebra-example", job.PrinterId);
      Assert.Equal(106, job.ContentLength);
      Assert.Equal(new PrintOptions(), job.Options);
      Assert.Null(await api.ClaimAsync(CancellationToken.None));
      Assert.Null(await api.ClaimAsync(CancellationToken.None));
   }



   /// <summary>Members the agent only displays may be absent: a missing or over-long creation time or status becomes empty text instead of a null that later code would trip over, and a hash in capitals is accepted because integrity is compared without regard to case.</summary>
   [Fact]
   public async Task DisplayOnlyMembersAreNormalized()
   {
      JsonObject job = ValidJob();
      job.Remove("createdAt");
      job["status"] = new string('S', 200);
      job["contentSha256"] = Hash.ToUpperInvariant();
      using var server = new FakePrintServer();
      server.OfferRaw(Claim(job));
      using PrintApiClient api = Api(server);
      PrintJob claimed = Assert.IsType<PrintJob>(await api.ClaimAsync(CancellationToken.None));
      Assert.Equal("", claimed.CreatedAt);
      Assert.Equal("", claimed.Status);
      Assert.Equal(Hash.ToUpperInvariant(), claimed.ContentSha256);
   }



   /// <summary>
   /// Every malformed member of a claimed job is refused with the one protocol-violation failure and a fixed message
   /// naming the member. When the job still named itself with a usable identifier the failure carries a stand-in for
   /// it; when the identifier itself is unusable, or a repeated field makes it untrustworthy, it does not.
   /// </summary>
   [Theory]
   [InlineData("jobId-missing", "The claimed job has no usable job identifier.", false)]
   [InlineData("jobId-empty", "The claimed job has no usable job identifier.", false)]
   [InlineData("jobId-path", "The claimed job has no usable job identifier.", false)]
   [InlineData("jobId-long", "The claimed job has no usable job identifier.", false)]
   [InlineData("jobId-number", "The claimed job does not have the expected fields.", false)]
   [InlineData("jobId-repeated", "The claim response repeats a field.", false)]
   [InlineData("printerId-missing", "The claimed job has no usable printer identifier.", true)]
   [InlineData("printerId-path", "The claimed job has no usable printer identifier.", true)]
   [InlineData("title-missing", "The claimed job has no title, or one that is too long.", true)]
   [InlineData("title-long", "The claimed job has no title, or one that is too long.", true)]
   [InlineData("title-number", "The claimed job does not have the expected fields.", true)]
   [InlineData("format-missing", "The claimed job has no usable document format.", true)]
   [InlineData("format-empty", "The claimed job has no usable document format.", true)]
   [InlineData("format-symbols", "The claimed job has no usable document format.", true)]
   [InlineData("hash-missing", "The claimed job has no SHA-256 content hash.", true)]
   [InlineData("hash-short", "The claimed job has no SHA-256 content hash.", true)]
   [InlineData("hash-not-hex", "The claimed job has no SHA-256 content hash.", true)]
   [InlineData("length-missing", "The claimed job declares an empty or impossibly large document.", true)]
   [InlineData("length-negative", "The claimed job declares an empty or impossibly large document.", true)]
   [InlineData("length-huge", "The claimed job declares an empty or impossibly large document.", true)]
   [InlineData("length-text", "The claimed job does not have the expected fields.", true)]
   [InlineData("length-fraction", "The claimed job does not have the expected fields.", true)]
   [InlineData("options-missing", "The claimed job has no print options.", true)]
   [InlineData("options-null", "The claimed job has no print options.", true)]
   [InlineData("options-array", "The claimed job does not have the expected fields.", true)]
   [InlineData("copies-zero", "The claimed job asks for an impossible number of copies.", true)]
   [InlineData("copies-huge", "The claimed job asks for an impossible number of copies.", true)]
   [InlineData("scale-null", "The claimed job has no usable scale option.", true)]
   [InlineData("scale-long", "The claimed job has no usable scale option.", true)]
   [InlineData("paper-partial", "The claimed job has incomplete or out-of-range paper dimensions, or combines them with a media profile.", true)]
   [InlineData("paper-range", "The claimed job has incomplete or out-of-range paper dimensions, or combines them with a media profile.", true)]
   [InlineData("paper-with-media", "The claimed job has incomplete or out-of-range paper dimensions, or combines them with a media profile.", true)]
   public async Task EachMalformedMemberOfAClaimedJobIsAProtocolViolation(string fault, string message, bool identified)
   {
      JsonObject job = ValidJob();
      Faults[fault](job);
      string body = string.Equals(fault, "jobId-repeated", StringComparison.Ordinal)
         ? Claim(job).Replace("\"printerId\"", "\"JOBID\":\"hostile-marker\",\"printerId\"", StringComparison.Ordinal)
         : Claim(job);
      using var server = new FakePrintServer();
      server.OfferRaw(body);
      using PrintApiClient api = Api(server);
      PrintingProtocolException violation = await Assert.ThrowsAsync<PrintingProtocolException>(() => api.ClaimAsync(CancellationToken.None));
      Assert.Equal(message, violation.Message);
      Assert.Equal(identified, violation.RejectedJob is not null);
      if(identified)
      {
         Assert.Equal("job-1", violation.RejectedJob!.JobId);
         Assert.Equal(new PrintOptions(), violation.RejectedJob.Options);
         Assert.Equal(0, violation.RejectedJob.ContentLength);
         Assert.DoesNotContain("hostile-marker", JsonSerializer.Serialize(violation.RejectedJob), StringComparison.Ordinal);
      }
   }



   /// <summary>A claim response that is not the expected JSON object at all (a web page, an array, a job that is not an object, nesting beyond the limit, a repeated job member) is the same protocol violation, with no job to report against.</summary>
   [Theory]
   [InlineData("<html>sign in</html>", "The claim response is not valid JSON.")]
   [InlineData("", "The claim response is not valid JSON.")]
   [InlineData("[]", "The claim response is not a JSON object.")]
   [InlineData("\"job\"", "The claim response is not a JSON object.")]
   [InlineData("""{"job":7}""", "The claimed job is not a JSON object.")]
   [InlineData("""{"job":[]}""", "The claimed job is not a JSON object.")]
   [InlineData("""{"job":null,"Job":null}""", "The claim response repeats a field.")]
   [InlineData("""{"job":{"a":{"b":{"c":{"d":{"e":{"f":{"g":{"h":{"i":{"j":{"k":{"l":{"m":{"n":{"o":{}}}}}}}}}}}}}}}}}""", "The claim response is not valid JSON.")]
   public async Task MalformedClaimResponseIsAProtocolViolation(string body, string message)
   {
      using var server = new FakePrintServer();
      server.OfferRaw(body);
      using PrintApiClient api = Api(server);
      PrintingProtocolException violation = await Assert.ThrowsAsync<PrintingProtocolException>(() => api.ClaimAsync(CancellationToken.None));
      Assert.Equal(message, violation.Message);
      Assert.Null(violation.RejectedJob);
   }



   /// <summary>
   /// A claimed job with no options, the shape that used to reach the worker and fail on a null, is reported to the server
   /// as received and then FAILED with the reason, shown in history with its title and printer, and never downloaded or
   /// printed. The next job in the queue is then claimed and spooled, so one bad job cannot block a device.
   /// </summary>
   [Fact]
   public async Task WorkerReportsARefusedJobAsFailedAndKeepsWorking()
   {
      byte[] document = [1, 2, 3];
      JsonObject malformed = ValidJob();
      malformed.Remove("options");
      using var server = new FakePrintServer();
      server.OfferRaw(Claim(malformed)).Offer(FakePrintServer.Job(document, "job-2"), document);
      using PrintApiClient api = Api(server);
      var journal = new RecordingJournal();
      var printer = new FakePrinterBackend();
      var worker = new PrintWorker(api, printer, journal);

      Assert.False(await worker.RunOnceAsync());
      Assert.Equal(new[] { "RECEIVED", "FAILED" }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal("The claimed job has no print options. No bytes were submitted.", journal.Last("job-1").Message);
      Assert.Equal(new[] { "RECEIVED", "FAILED" }, server.States, StringComparer.Ordinal);
      Assert.Empty(journal.Pending());
      Assert.Equal("Synthetic", journal.Jobs["job-1"].Job.Title);
      Assert.Equal("Queue one", journal.Jobs["job-1"].PrinterName);
      Assert.Equal(0, server.Downloads);
      Assert.Equal(0, printer.Submissions);

      Assert.True(await worker.RunOnceAsync());
      Assert.Equal("SPOOLED", journal.GetState("job-2"));
      Assert.Equal(1, printer.Submissions);
   }



   /// <summary>A realtime lane, which drains until the queue is empty, counts a refused job as processed, so it goes on to claim the job behind it instead of stopping the wave.</summary>
   [Fact]
   public async Task LaneKeepsDrainingAfterARefusedJob()
   {
      JsonObject malformed = ValidJob();
      malformed["contentLength"] = 0;
      using var server = new FakePrintServer();
      server.OfferRaw(Claim(malformed));
      using PrintApiClient api = Api(server);
      var journal = new RecordingJournal();
      var worker = new PrintWorker(api, new FakePrinterBackend(), journal, options: new PrintWorkerOptions { RecoverIncomplete = false, SynchronizeInventory = false });
      Assert.True(await worker.RunOnceAsync());
      Assert.Equal("FAILED", journal.GetState("job-1"));
      Assert.False(await worker.RunOnceAsync());
   }



   /// <summary>A malformed replay of a job the journal already holds in a final state changes nothing: no new event, no new receipt, and the job's recorded title and printer are not overwritten by the stand-in.</summary>
   [Theory]
   [InlineData("SPOOLED")]
   [InlineData("OUTCOME_UNKNOWN")]
   public async Task MalformedReplayOfAFinishedJobChangesNothing(string state)
   {
      JsonObject malformed = ValidJob();
      malformed.Remove("options");
      malformed["title"] = "Replayed title";
      using var server = new FakePrintServer();
      server.OfferRaw(Claim(malformed));
      using PrintApiClient api = Api(server);
      var journal = new RecordingJournal();
      journal.TrackJob(FakePrintServer.Job([1], "job-1") with { Title = "Original title" }, "Original printer");
      journal.Record("job-1", new PrintEvent("earlier", state));
      journal.Acknowledge("earlier");
      var printer = new FakePrinterBackend();
      Assert.False(await new PrintWorker(api, printer, journal, options: new PrintWorkerOptions { RecoverIncomplete = false }).RunOnceAsync());
      Assert.Equal(new[] { state }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Empty(server.Events);
      Assert.Equal("Original title", journal.Jobs["job-1"].Job.Title);
      Assert.Equal("Original printer", journal.Jobs["job-1"].PrinterName);
      Assert.Equal(0, printer.Submissions);
   }



   /// <summary>A claimed job that cannot even be identified has nothing to be reported against: the violation reaches the caller, and nothing is recorded, downloaded or printed.</summary>
   [Fact]
   public async Task UnidentifiableJobIsNotRecordedAndTheViolationReachesTheCaller()
   {
      JsonObject malformed = ValidJob();
      malformed["jobId"] = "not a safe identifier";
      using var server = new FakePrintServer();
      server.OfferRaw(Claim(malformed));
      using PrintApiClient api = Api(server);
      var journal = new RecordingJournal();
      var printer = new FakePrinterBackend();
      await Assert.ThrowsAsync<PrintingProtocolException>(() => new PrintWorker(api, printer, journal).RunOnceAsync());
      Assert.Empty(journal.Jobs);
      Assert.Empty(journal.Pending());
      Assert.Empty(server.Events);
      Assert.Equal(0, printer.Submissions);
   }



   /// <summary>When the refusal cannot be delivered because the server stopped answering, both events stay in the journal and are delivered, in order, by the next run; the job is not offered to the printer in between.</summary>
   [Fact]
   public async Task UndeliveredRefusalStaysInTheOutboxAndIsDeliveredLater()
   {
      JsonObject malformed = ValidJob();
      malformed.Remove("options");
      bool down = true;
      using var server = new FakePrintServer
      {
         Arriving = (kind, _) => down && string.Equals(kind, "events", StringComparison.Ordinal) ? throw new HttpRequestException("Synthetic outage.") : Task.CompletedTask,
      };
      server.OfferRaw(Claim(malformed));
      using PrintApiClient api = Api(server);
      var journal = new RecordingJournal();
      var printer = new FakePrinterBackend();
      var worker = new PrintWorker(api, printer, journal);
      await Assert.ThrowsAsync<HttpRequestException>(() => worker.RunOnceAsync());
      Assert.Equal(2, journal.Pending().Count);
      down = false;
      Assert.False(await worker.RunOnceAsync());
      Assert.Equal(new[] { "RECEIVED", "FAILED" }, server.States, StringComparer.Ordinal);
      Assert.Empty(journal.Pending());
      Assert.Equal(0, printer.Submissions);
   }



   /// <summary>If something else records a final state for a job while its receipt is being acknowledged, the worker does not go on to download or submit it.</summary>
   [Fact]
   public async Task JobFinalizedWhileItsReceiptWasInFlightIsNotSubmitted()
   {
      byte[] document = [1, 2, 3];
      using var server = new FakePrintServer();
      server.Offer(FakePrintServer.Job(document), document);
      using PrintApiClient api = Api(server);
      var journal = new RecordingJournal();
      journal.Recorded = (jobId, printEvent) =>
      {
         if(string.Equals(printEvent.Status, "RECEIVED", StringComparison.Ordinal))
         { journal.Record(jobId, new PrintEvent("finalized-elsewhere", "FAILED", "Cancelled by the operator.")); }
      };
      var printer = new FakePrinterBackend();
      Assert.False(await new PrintWorker(api, printer, journal).RunOnceAsync());
      Assert.Equal(new[] { "RECEIVED", "FAILED" }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal(0, server.Downloads);
      Assert.Equal(0, printer.Submissions);
   }



   /// <summary>The queued-job list is accepted from a server that offers it, treated as empty from one that does not (404) or that sends no list, and its display-only members may be missing.</summary>
   [Fact]
   public async Task QueuedJobsAreReadLenientlyForDisplay()
   {
      using var server = new ScriptedServer();
      server.Add("{}", HttpStatusCode.NotFound).Add("{}").Add("""{"jobs":null}""")
         .Add("""{"jobs":[{"jobId":"queued-1","printerId":"printer1"},{"jobId":"queued-2","printerId":"printer1","title":"Second","format":"PDF","contentSha256":"unused","contentLength":5,"options":{"copies":2},"createdAt":"2026-01-01T00:00:00Z","status":"QUEUED"}]}""");
      using var api = new PrintApiClient("https://synthetic.invalid", handler: server);
      Assert.Empty(await api.QueuedJobsAsync(CancellationToken.None));
      Assert.Empty(await api.QueuedJobsAsync(CancellationToken.None));
      Assert.Empty(await api.QueuedJobsAsync(CancellationToken.None));
      IReadOnlyList<PrintJob> jobs = await api.QueuedJobsAsync(CancellationToken.None);
      Assert.Equal(new PrintJob("queued-1", "printer1", "", "", "", 0, new PrintOptions(), "", ""), jobs[0]);
      Assert.Equal(new PrintJob("queued-2", "printer1", "Second", "PDF", "unused", 5, new PrintOptions(Copies: 2), "2026-01-01T00:00:00Z", "QUEUED"), jobs[1]);
      Assert.Equal(2, jobs.Count);
      Assert.All(server.Requests, request => Assert.Equal("/printing/v1/device/jobs/queued", request.Uri.AbsolutePath));
   }



   /// <summary>A queued-job list that breaks the contract (not JSON, not a list, too long, an entry that is not an object or has no usable identifiers, a repeated field, a member of the wrong type) is a protocol violation, and a server error is still reported as an HTTP failure.</summary>
   [Theory]
   [InlineData("<html></html>", "The queued jobs response is not valid JSON.")]
   [InlineData("[]", "The queued jobs response is not a JSON object.")]
   [InlineData("""{"jobs":{}}""", "The queued jobs response does not hold a list of at most 200 jobs.")]
   [InlineData("too-many", "The queued jobs response does not hold a list of at most 200 jobs.")]
   [InlineData("""{"jobs":[null]}""", "A queued job is not a JSON object.")]
   [InlineData("""{"jobs":["queued-1"]}""", "A queued job is not a JSON object.")]
   [InlineData("""{"jobs":[{"printerId":"printer1"}]}""", "A queued job has no usable job or printer identifier.")]
   [InlineData("""{"jobs":[{"jobId":"queued-1","printerId":"a b"}]}""", "A queued job has no usable job or printer identifier.")]
   [InlineData("""{"jobs":[{"jobId":"queued-1","JobId":"queued-2","printerId":"printer1"}]}""", "The queued jobs response repeats a field.")]
   [InlineData("""{"jobs":[{"jobId":"queued-1","printerId":"printer1","contentLength":"five"}]}""", "The queued job does not have the expected fields.")]
   public async Task MalformedQueuedJobListIsAProtocolViolation(string body, string message)
   {
      if(string.Equals(body, "too-many", StringComparison.Ordinal))
      { body = "{\"jobs\":[" + string.Join(",", Enumerable.Range(0, 201).Select(index => $$"""{"jobId":"queued-{{index}}","printerId":"printer1"}""")) + "]}"; }
      using var server = new ScriptedServer();
      server.Add(body).Add("{}", HttpStatusCode.InternalServerError);
      using var api = new PrintApiClient("https://synthetic.invalid", handler: server);
      PrintingProtocolException violation = await Assert.ThrowsAsync<PrintingProtocolException>(() => api.QueuedJobsAsync(CancellationToken.None));
      Assert.Equal(message, violation.Message);
      Assert.Null(violation.RejectedJob);
      await Assert.ThrowsAsync<HttpRequestException>(() => api.QueuedJobsAsync(CancellationToken.None));
   }



   /// <summary>Each way one member of a claimed job can be malformed, by the name the theory uses: the change it makes to an otherwise valid job.</summary>
   private static readonly Dictionary<string, Action<JsonObject>> Faults = new(StringComparer.Ordinal)
   {
      ["jobId-missing"] = job => job.Remove("jobId"),
      ["jobId-empty"] = job => job["jobId"] = "",
      ["jobId-path"] = job => job["jobId"] = "../../hostile-marker",
      ["jobId-long"] = job => job["jobId"] = new string('j', 201),
      ["jobId-number"] = job => job["jobId"] = 7,
      ["jobId-repeated"] = _ => { },
      ["printerId-missing"] = job => job.Remove("printerId"),
      ["printerId-path"] = job => job["printerId"] = "hostile-marker/..\\x",
      ["title-missing"] = job => job.Remove("title"),
      ["title-long"] = job => job["title"] = "hostile-marker" + new string('t', 1024),
      ["title-number"] = job => job["title"] = 7,
      ["format-missing"] = job => job.Remove("format"),
      ["format-empty"] = job => job["format"] = "",
      ["format-symbols"] = job => job["format"] = "PDF;hostile-marker",
      ["hash-missing"] = job => job.Remove("contentSha256"),
      ["hash-short"] = job => job["contentSha256"] = "00",
      ["hash-not-hex"] = job => job["contentSha256"] = new string('z', 64),
      ["length-missing"] = job => job.Remove("contentLength"),
      ["length-negative"] = job => job["contentLength"] = -1,
      ["length-huge"] = job => job["contentLength"] = 2_147_483_648,
      ["length-text"] = job => job["contentLength"] = "three",
      ["length-fraction"] = job => job["contentLength"] = 1.5,
      ["options-missing"] = job => job.Remove("options"),
      ["options-null"] = job => job["options"] = null,
      ["options-array"] = job => job["options"] = new JsonArray(),
      ["copies-zero"] = job => job["options"]!["copies"] = 0,
      ["copies-huge"] = job => job["options"]!["copies"] = 32_768,
      ["scale-null"] = job => job["options"]!["scale"] = null,
      ["scale-long"] = job => job["options"]!["scale"] = "hostile-marker" + new string('S', 40),
      ["paper-partial"] = job => job["options"]!["paperWidthMm"] = 100,
      ["paper-range"] = job => job["options"] = new JsonObject { ["paperWidthMm"] = 3000, ["paperHeightMm"] = 10 },
      ["paper-with-media"] = job => job["options"] = new JsonObject { ["paperWidthMm"] = 100, ["paperHeightMm"] = 150, ["mediaProfile"] = new JsonObject { ["id"] = "default", ["revision"] = Hash } },
   };


   /// <summary>A well-formed job as the contract describes it; tests remove or replace one member at a time.</summary>
   private static JsonObject ValidJob() => new()
   {
      ["jobId"] = "job-1",
      ["printerId"] = "printer1",
      ["title"] = "Synthetic",
      ["format"] = "ZPL",
      ["contentSha256"] = Hash,
      ["contentLength"] = 3,
      ["options"] = new JsonObject { ["copies"] = 1, ["scale"] = "ACTUAL_SIZE" },
      ["createdAt"] = "2026-01-01T00:00:00Z",
      ["status"] = "CLAIMED",
   };



   /// <summary>Wraps a job in a claim response.</summary>
   private static string Claim(JsonObject job) => new JsonObject { ["job"] = job.DeepClone() }.ToJsonString();



   /// <summary>A device-token API client over the fake server; nothing leaves the process.</summary>
   private static PrintApiClient Api(FakePrintServer server) => new("http://localhost", "synthetic-device-token", server);
}
