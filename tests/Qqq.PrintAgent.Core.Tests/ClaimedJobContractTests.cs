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



   /// <summary>
   /// Members the agent only displays may be absent or over-long: a missing creation time or status, or one a single
   /// character past its limit (64 for the time, 32 for the status), becomes empty text instead of a null that later
   /// code would trip over or a value too long to show. The job itself is still accepted.
   /// </summary>
   [Theory]
   [InlineData("missing")]
   [InlineData("one-past-the-limit")]
   [InlineData("far-past-the-limit")]
   public async Task DisplayOnlyMembersAreNormalized(string fault)
   {
      JsonObject job = ValidJob();
      switch(fault)
      {
         case "missing":
            job.Remove("createdAt");
            job.Remove("status");
            break;
         case "one-past-the-limit":
            job["createdAt"] = new string('c', 65);
            job["status"] = new string('S', 33);
            break;
         default:
            job["createdAt"] = new string('c', 2000);
            job["status"] = new string('S', 200);
            break;
      }
      using var server = new FakePrintServer();
      server.OfferRaw(Claim(job));
      using PrintApiClient api = Api(server);
      PrintJob claimed = Assert.IsType<PrintJob>(await api.ClaimAsync(CancellationToken.None));
      Assert.Equal("", claimed.CreatedAt);
      Assert.Equal("", claimed.Status);
      Assert.Equal("job-1", claimed.JobId);
   }



   /// <summary>
   /// The contract writes the content hash in lowercase hexadecimal. The agent also accepts capitals, and this test
   /// says so on purpose: it is looser than the contract, the agent always was, and it costs nothing, because the hash
   /// is only ever compared with the hash of the downloaded bytes, without regard to case. So a job whose hash arrives
   /// in capitals is claimed with the hash as sent, and it prints when the document matches and fails when it does not.
   /// </summary>
   [Theory]
   [InlineData(true, "SPOOLED")]
   [InlineData(false, "FAILED")]
   public async Task HashInCapitalsIsAcceptedOnPurposeAndStillVerifiesTheDocument(bool documentMatches, string outcome)
   {
      byte[] document = [1, 2, 3];
      PrintJob lowercase = FakePrintServer.Job(document);
      Assert.Equal(lowercase.ContentSha256.ToLowerInvariant(), lowercase.ContentSha256);
      PrintJob capitals = lowercase with { ContentSha256 = lowercase.ContentSha256.ToUpperInvariant() };
      Assert.NotEqual(lowercase.ContentSha256, capitals.ContentSha256, StringComparer.Ordinal);
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.Offer(capitals, documentMatches ? document : [3, 2, 1]);
      Assert.True(await harness.RunOnceAsync());
      Assert.Equal(capitals.ContentSha256, journal.Jobs["job-1"].Job.ContentSha256);
      Assert.Equal(outcome, journal.GetState("job-1"));
      Assert.Equal(documentMatches ? 1 : 0, printer.Submissions);
   }



   /// <summary>
   /// The accepting side of every limit a claimed job has. Each row puts one member exactly at its limit, or uses every
   /// character an identifier may contain, and the job is claimed with that member unchanged. The refusing side, one
   /// step past each of these, is in the theory below; together they fail if any limit is moved.
   /// </summary>
   [Theory]
   [InlineData("jobId-200-characters")]
   [InlineData("jobId-every-allowed-character")]
   [InlineData("printerId-200-characters")]
   [InlineData("printerId-every-allowed-character")]
   [InlineData("title-1024-characters")]
   [InlineData("format-32-characters")]
   [InlineData("format-1-character")]
   [InlineData("length-1")]
   [InlineData("length-2147483647")]
   [InlineData("copies-32767")]
   [InlineData("scale-32-characters")]
   [InlineData("scale-1-character")]
   [InlineData("paper-2000-millimetres")]
   [InlineData("paper-smallest")]
   [InlineData("createdAt-64-characters")]
   [InlineData("status-32-characters")]
   public async Task ClaimedJobAtEachLimitIsAcceptedUnchanged(string limit)
   {
      JsonObject job = ValidJob();
      AtLimit[limit](job);
      using var server = new FakePrintServer();
      server.OfferRaw(Claim(job));
      using PrintApiClient api = Api(server);
      PrintJob claimed = Assert.IsType<PrintJob>(await api.ClaimAsync(CancellationToken.None));
      Assert.True(JsonNode.DeepEquals(JsonNode.Parse(job.ToJsonString()), JsonNode.Parse(JsonSerializer.Serialize(claimed, PrintApiClient.JsonOptions))),
         $"The claimed job differs from the one offered: {JsonSerializer.Serialize(claimed, PrintApiClient.JsonOptions)}");
   }



   /// <summary>
   /// The contract limits a title to 1,024 code points, as JSON Schema counts length, not to 1,024 UTF-16 units. A
   /// title of 1,024 characters from outside the basic plane, two units each, is therefore accepted whole, and one more
   /// such character is refused, with a stand-in that does not carry the over-long title.
   /// </summary>
   [Fact]
   public async Task TitleLimitCountsCodePointsNotUtf16Units()
   {
      string atLimit = string.Concat(Enumerable.Repeat("\U0001F5A8", 1024));
      JsonObject accepted = ValidJob();
      accepted["title"] = atLimit;
      JsonObject refused = ValidJob();
      refused["title"] = atLimit + "\U0001F5A8";
      using var server = new FakePrintServer();
      server.OfferRaw(Claim(accepted)).OfferRaw(Claim(refused));
      using PrintApiClient api = Api(server);
      PrintJob job = Assert.IsType<PrintJob>(await api.ClaimAsync(CancellationToken.None));
      Assert.Equal(atLimit, job.Title);
      Assert.Equal(2048, job.Title.Length);
      PrintingProtocolException violation = await Assert.ThrowsAsync<PrintingProtocolException>(() => api.ClaimAsync(CancellationToken.None));
      Assert.Equal("The claimed job has no title, or one that is too long.", violation.Message);
      Assert.Equal("", violation.RejectedJob!.Title);
   }



   /// <summary>
   /// Every malformed member of a claimed job is refused with the one protocol-violation failure and a fixed message
   /// naming the member. When the job still named itself with a usable identifier the failure carries a stand-in for
   /// it; when the identifier itself is unusable it does not.
   /// </summary>
   [Theory]
   [InlineData("jobId-missing", "The claimed job has no usable job identifier.", false)]
   [InlineData("jobId-empty", "The claimed job has no usable job identifier.", false)]
   [InlineData("jobId-path", "The claimed job has no usable job identifier.", false)]
   [InlineData("jobId-long", "The claimed job has no usable job identifier.", false)]
   [InlineData("jobId-number", "The claimed job does not have the expected fields.", false)]
   [InlineData("jobId-201-characters", "The claimed job has no usable job identifier.", false)]
   [InlineData("jobId-space", "The claimed job has no usable job identifier.", false)]
   [InlineData("jobId-letter-outside-ascii", "The claimed job has no usable job identifier.", false)]
   [InlineData("printerId-missing", "The claimed job has no usable printer identifier.", true)]
   [InlineData("printerId-path", "The claimed job has no usable printer identifier.", true)]
   [InlineData("printerId-201-characters", "The claimed job has no usable printer identifier.", true)]
   [InlineData("printerId-comma", "The claimed job has no usable printer identifier.", true)]
   [InlineData("title-missing", "The claimed job has no title, or one that is too long.", true)]
   [InlineData("title-long", "The claimed job has no title, or one that is too long.", true)]
   [InlineData("title-1025-characters", "The claimed job has no title, or one that is too long.", true)]
   [InlineData("title-number", "The claimed job does not have the expected fields.", true)]
   [InlineData("format-missing", "The claimed job has no usable document format.", true)]
   [InlineData("format-empty", "The claimed job has no usable document format.", true)]
   [InlineData("format-symbols", "The claimed job has no usable document format.", true)]
   [InlineData("format-33-characters", "The claimed job has no usable document format.", true)]
   [InlineData("format-hyphen", "The claimed job has no usable document format.", true)]
   [InlineData("hash-missing", "The claimed job has no SHA-256 content hash.", true)]
   [InlineData("hash-short", "The claimed job has no SHA-256 content hash.", true)]
   [InlineData("hash-63-characters", "The claimed job has no SHA-256 content hash.", true)]
   [InlineData("hash-65-characters", "The claimed job has no SHA-256 content hash.", true)]
   [InlineData("hash-not-hex", "The claimed job has no SHA-256 content hash.", true)]
   [InlineData("length-missing", "The claimed job declares an empty or impossibly large document.", true)]
   [InlineData("length-zero", "The claimed job declares an empty or impossibly large document.", true)]
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
   [InlineData("scale-33-characters", "The claimed job has no usable scale option.", true)]
   [InlineData("scale-hyphen", "The claimed job has no usable scale option.", true)]
   [InlineData("paper-partial", "The claimed job has incomplete or out-of-range paper dimensions, or combines them with a media profile.", true)]
   [InlineData("paper-range", "The claimed job has incomplete or out-of-range paper dimensions, or combines them with a media profile.", true)]
   [InlineData("paper-just-over-2000-millimetres", "The claimed job has incomplete or out-of-range paper dimensions, or combines them with a media profile.", true)]
   [InlineData("paper-zero", "The claimed job has incomplete or out-of-range paper dimensions, or combines them with a media profile.", true)]
   [InlineData("paper-with-media", "The claimed job has incomplete or out-of-range paper dimensions, or combines them with a media profile.", true)]
   public async Task EachMalformedMemberOfAClaimedJobIsAProtocolViolation(string fault, string message, bool identified)
   {
      JsonObject job = ValidJob();
      Faults[fault](job);
      using var server = new FakePrintServer();
      server.OfferRaw(Claim(job));
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



   /// <summary>
   /// A claim response that is not the expected JSON object at all (a web page, an array, a job that is not an object,
   /// two job members) is the same protocol violation, with no job to report against. The last two rows are the one
   /// case left in which a job is not answered for: a response nested past the 1,024 levels it is parsed to, by objects
   /// or by arrays, is not JSON to the parser, so it names no job, the lane stops, and the server offers the job again
   /// until it is cancelled there. Options that deep do not fit in the 2,000 characters the server allows a submitter,
   /// which the test after next shows; the stall is closed only as long as that cap stands.
   /// </summary>
   [Theory]
   [InlineData("<html>sign in</html>", "The claim response is not valid JSON.")]
   [InlineData("", "The claim response is not valid JSON.")]
   [InlineData("[]", "The claim response is not a JSON object.")]
   [InlineData("\"job\"", "The claim response is not a JSON object.")]
   [InlineData("""{"job":7}""", "The claimed job is not a JSON object.")]
   [InlineData("""{"job":[]}""", "The claimed job is not a JSON object.")]
   [InlineData("""{"job":null,"job":null}""", "The claim response names more than one job.")]
   [InlineData("""{"Job":null,"JOB":null}""", "The claim response names more than one job.")]
   [InlineData("nested-1025", "The claim response is not valid JSON.")]
   [InlineData("arrays-nested-1025", "The claim response is not valid JSON.")]
   public async Task MalformedClaimResponseIsAProtocolViolation(string body, string message)
   {
      if(string.Equals(body, "nested-1025", StringComparison.Ordinal))
      { body = ClaimWithNestedOption(1025); }
      if(string.Equals(body, "arrays-nested-1025", StringComparison.Ordinal))
      { body = ClaimWithOptions(OptionsNestingArrays(1022)); }
      using var server = new FakePrintServer();
      server.OfferRaw(body);
      using PrintApiClient api = Api(server);
      PrintingProtocolException violation = await Assert.ThrowsAsync<PrintingProtocolException>(() => api.ClaimAsync(CancellationToken.None));
      Assert.Equal(message, violation.Message);
      Assert.Null(violation.RejectedJob);
   }



   /// <summary>
   /// The contract tells consumers to ignore fields they do not know, and the server relays a submitter's options
   /// unchanged, so a job is accepted whatever else it carries: unknown members beside the job, in the job and in its
   /// options, unknown names that repeat or differ only in case, and an unknown option nested as deep as the JSON
   /// reader itself accepts (64 levels counted from the response). None of it reaches the job the worker sees. One
   /// level deeper the job is refused, in the test after this one.
   /// </summary>
   [Theory]
   [InlineData("""{"serverTime":"2026-01-01T00:00:00Z","job":JOB}""")]
   [InlineData("""{"job":JOB,"Job":null}""")]
   [InlineData("""{"job":{"deviceId":"device-1","message":"relayed","Message":"relayed again","message":7,JOB-MEMBERS}}""")]
   [InlineData("""{"job":{JOB-MEMBERS-WITHOUT-OPTIONS,"options":{"copies":1,"scale":"ACTUAL_SIZE","duplex":true,"Duplex":false,"duplex":"LONG_EDGE","tray":{"id":"upper","ID":"lower"}}}}""")]
   [InlineData("""{"job":{JOB-MEMBERS-WITHOUT-OPTIONS,"options":{"copies":1,"scale":"ACTUAL_SIZE","finishing":{"a":{"b":{"c":{"d":{"e":{"f":{"g":{"h":{"i":{"j":{"k":{"l":{"m":{"n":{}}}}}}}}}}}}}}}}}}""")]
   [InlineData("nested-64")]
   public async Task FieldsTheAgentDoesNotReadAreIgnoredAtAnyDepthTheReaderAccepts(string body)
   {
      body = string.Equals(body, "nested-64", StringComparison.Ordinal) ? ClaimWithNestedOption(64) : Expand(body);
      using var server = new FakePrintServer();
      server.OfferRaw(body);
      using PrintApiClient api = Api(server);
      Assert.Equal(new PrintJob("job-1", "printer1", "Synthetic", "ZPL", Hash, 3, new PrintOptions(), "2026-01-01T00:00:00Z", "CLAIMED"), await api.ClaimAsync(CancellationToken.None));
   }



   /// <summary>
   /// A job nested one level deeper than the agent reads (65 levels counted from the response), or anywhere up to the
   /// 1,024 levels the response is parsed to, is refused, and it is still identified: the response is parsed far enough
   /// to learn which job it is, so the worker can answer for it. A submitter chooses how deep a job's options nest, so
   /// without this one request could make the server offer a job the agent never answers.
   /// </summary>
   [Theory]
   [InlineData(65)]
   [InlineData(66)]
   [InlineData(513)]
   [InlineData(1024)]
   public async Task JobNestedDeeperThanTheAgentReadsIsRefusedAndStillIdentified(int depth)
   {
      using var server = new FakePrintServer();
      server.OfferRaw(ClaimWithNestedOption(depth));
      using PrintApiClient api = Api(server);
      PrintingProtocolException violation = await Assert.ThrowsAsync<PrintingProtocolException>(() => api.ClaimAsync(CancellationToken.None));
      Assert.Equal("The claimed job is nested deeper than the agent reads.", violation.Message);
      PrintJob refused = Assert.IsType<PrintJob>(violation.RejectedJob);
      Assert.Equal("job-1", refused.JobId);
      Assert.Equal("printer1", refused.PrinterId);
      Assert.Equal("Synthetic", refused.Title);
      Assert.Equal(new PrintOptions(), refused.Options);
   }



   /// <summary>
   /// The deepest options a submitter can get past the server. The server accepts at most 2,000 characters of options
   /// and its JSON library has no depth limit, and the cheapest way to nest is an array inside an array, two characters
   /// a level. Each row is options of that shape: 510 arrays (1,059 characters, a claim 513 levels deep, the case that
   /// was not identified when the response was parsed to 512), the 970 arrays that just fit the cap (1,979 characters),
   /// and 1,021 arrays, more than the cap allows, which bring the claim to exactly the 1,024 levels it is parsed to.
   /// Every one is identified and refused.
   /// </summary>
   [Theory]
   [InlineData(510, 1059, 513)]
   [InlineData(970, 1979, 973)]
   [InlineData(1021, 2081, 1024)]
   public async Task OptionsNestedByArraysAsDeepAsTheServerAllowsAreIdentifiedAndRefused(int arrays, int characters, int depth)
   {
      string options = OptionsNestingArrays(arrays);
      Assert.Equal(characters, options.Length);
      Assert.Equal(depth, 3 + arrays);
      Assert.Equal(characters <= 2000, arrays <= 970);
      using var server = new FakePrintServer();
      server.OfferRaw(ClaimWithOptions(options));
      using PrintApiClient api = Api(server);
      PrintingProtocolException violation = await Assert.ThrowsAsync<PrintingProtocolException>(() => api.ClaimAsync(CancellationToken.None));
      Assert.Equal("The claimed job is nested deeper than the agent reads.", violation.Message);
      Assert.Equal("job-1", Assert.IsType<PrintJob>(violation.RejectedJob).JobId);
   }



   /// <summary>
   /// What the worker does at each side of the depth it parses to. A job nested deeper than it reads is refused exactly
   /// once (received, then FAILED with nothing submitted, never downloaded or printed) and the lane goes on to the job
   /// behind it. Only a response nested past the 1,024 levels it is parsed to cannot be answered for: the violation
   /// reaches the caller, nothing is recorded or reported, and the lane stops.
   /// </summary>
   [Fact]
   public async Task OverDeepJobIsRefusedOnceAndOnlyAnUnparseableResponseStopsTheLane()
   {
      byte[] document = [1, 2, 3];
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.OfferRaw(ClaimWithNestedOption(65)).Offer(FakePrintServer.Job(document, "job-2"), document).OfferRaw(ClaimWithNestedOption(1025).Replace("job-1", "job-3", StringComparison.Ordinal));

      Assert.True(await harness.RunOnceAsync());
      Assert.Equal(new[] { "RECEIVED", "FAILED" }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal("The claimed job is nested deeper than the agent reads. No bytes were submitted.", journal.Last("job-1").Message);
      Assert.Equal(new[] { ("job-1", "RECEIVED"), ("job-1", "FAILED") }, server.Events.Select(entry => (entry.JobId, entry.Event.Status)));
      Assert.Equal(0, server.Downloads);
      Assert.Equal(0, printer.Submissions);

      Assert.True(await harness.RunOnceAsync());
      Assert.Equal("SPOOLED", journal.GetState("job-2"));
      Assert.Equal(1, printer.Submissions);

      PrintingProtocolException unparseable = await Assert.ThrowsAsync<PrintingProtocolException>(() => harness.RunOnceAsync());
      Assert.Equal("The claim response is not valid JSON.", unparseable.Message);
      Assert.Null(unparseable.RejectedJob);
      Assert.Null(journal.GetState("job-3"));
      Assert.DoesNotContain(server.Events, entry => string.Equals(entry.JobId, "job-3", StringComparison.Ordinal));
      Assert.Equal(3, server.Claims);
      Assert.Equal(1, printer.Submissions);
   }



   /// <summary>
   /// The agent matches member names without regard to case, so a job that gives a member the agent reads twice (in the
   /// job, in its options or in its media selection) could be printed with a value the server did not mean. Such a job
   /// is refused, and it is still identified: the stand-in carries the identifier the server spelled as the contract
   /// spells it, so the worker can answer for the job instead of being offered it forever. Only a job whose identifier
   /// is itself given twice in the contract's spelling cannot be answered for.
   /// </summary>
   [Theory]
   [InlineData("""{JOB-MEMBERS,"JOBID":"hostile-marker"}""", true)]
   [InlineData("""{"Title":"hostile-marker",JOB-MEMBERS}""", true)]
   [InlineData("""{JOB-MEMBERS,"options":{"copies":3}}""", true)]
   [InlineData("""{JOB-MEMBERS-WITHOUT-OPTIONS,"options":{"copies":1,"Copies":2}}""", true)]
   [InlineData("""{JOB-MEMBERS-WITHOUT-OPTIONS,"options":{"copies":1,"copies":1}}""", true)]
   [InlineData("""{JOB-MEMBERS-WITHOUT-OPTIONS,"options":{"scale":"FIT","SCALE":"ACTUAL_SIZE"}}""", true)]
   [InlineData("""{JOB-MEMBERS-WITHOUT-OPTIONS,"options":{"mediaProfile":{"id":"default","revision":"HASH","Id":"other"}}}""", true)]
   [InlineData("""{JOB-MEMBERS-WITHOUT-OPTIONS,"options":{"mediaProfile":{"id":"default","revision":"HASH","revision":"HASH"}}}""", true)]
   [InlineData("""{JOB-MEMBERS,"jobId":"job-2"}""", false)]
   public async Task JobThatRepeatsAMemberTheAgentReadsIsRefusedAndStillIdentified(string job, bool identified)
   {
      using var server = new FakePrintServer();
      server.OfferRaw("{\"job\":" + Expand(job) + "}");
      using PrintApiClient api = Api(server);
      PrintingProtocolException violation = await Assert.ThrowsAsync<PrintingProtocolException>(() => api.ClaimAsync(CancellationToken.None));
      Assert.Equal("The claimed job repeats a member the agent reads.", violation.Message);
      Assert.Equal(identified, violation.RejectedJob is not null);
      if(identified)
      {
         Assert.Equal("job-1", violation.RejectedJob!.JobId);
         Assert.Equal("printer1", violation.RejectedJob.PrinterId);
         Assert.Equal("Synthetic", violation.RejectedJob.Title);
         Assert.Equal(new PrintOptions(), violation.RejectedJob.Options);
         Assert.DoesNotContain("hostile-marker", JsonSerializer.Serialize(violation.RejectedJob), StringComparison.Ordinal);
      }
   }



   /// <summary>
   /// A server that spells a member in another case, and gives it once, is read as it was before this boundary existed:
   /// the job is claimed. This is looser than the contract's camelCase and deliberate, because the agent has always
   /// matched names this way and a job that printed must not start to be silently passed over.
   /// </summary>
   [Fact]
   public async Task MemberSpelledInAnotherCaseOnceIsStillRead()
   {
      using var server = new FakePrintServer();
      server.OfferRaw(Claim(ValidJob()).Replace("\"job\"", "\"Job\"", StringComparison.Ordinal).Replace("\"jobId\"", "\"JobId\"", StringComparison.Ordinal).Replace("\"copies\":1", "\"COPIES\":2", StringComparison.Ordinal));
      using PrintApiClient api = Api(server);
      PrintJob job = Assert.IsType<PrintJob>(await api.ClaimAsync(CancellationToken.None));
      Assert.Equal("job-1", job.JobId);
      Assert.Equal(2, job.Options.Copies);
   }



   /// <summary>A UTF-8 byte-order mark in front of the claim or the queued-job list is skipped, as it was before the contract checks; one anywhere else is still not JSON.</summary>
   [Fact]
   public async Task ByteOrderMarkBeforeTheResponseIsAccepted()
   {
      using var server = new FakePrintServer();
      server.OfferRaw("\uFEFF" + Claim(ValidJob())).OfferRaw("\uFEFF").OfferRaw(" \uFEFF" + Claim(ValidJob()));
      using PrintApiClient api = Api(server);
      Assert.Equal("job-1", Assert.IsType<PrintJob>(await api.ClaimAsync(CancellationToken.None)).JobId);
      Assert.Equal("The claim response is not valid JSON.", (await Assert.ThrowsAsync<PrintingProtocolException>(() => api.ClaimAsync(CancellationToken.None))).Message);
      Assert.Equal("The claim response is not valid JSON.", (await Assert.ThrowsAsync<PrintingProtocolException>(() => api.ClaimAsync(CancellationToken.None))).Message);

      using var queue = new ScriptedServer();
      queue.Add("\uFEFF" + """{"jobs":[{"jobId":"queued-1","printerId":"printer1"}]}""");
      using var queued = new PrintApiClient("https://synthetic.invalid", handler: queue);
      Assert.Equal("queued-1", Assert.Single(await queued.QueuedJobsAsync(CancellationToken.None)).JobId);
   }



   /// <summary>
   /// The lists of members the repeated-name rule covers are exactly the members the job, options and media selection
   /// records have, so a member added to a record cannot be left out of the rule without this test failing.
   /// </summary>
   [Fact]
   public void RepeatedNameRuleCoversExactlyTheMembersTheRecordsRead()
   {
      static string[] Names(Type record) => [.. record.GetProperties().Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)).Order(StringComparer.Ordinal)];
      Assert.Equal(Names(typeof(PrintJob)), PrintJobContract.JobMembers.Order(StringComparer.Ordinal), StringComparer.Ordinal);
      Assert.Equal(Names(typeof(PrintOptions)), PrintJobContract.OptionMembers.Order(StringComparer.Ordinal), StringComparer.Ordinal);
      Assert.Equal(Names(typeof(PrinterMediaSelection)), PrintJobContract.MediaMembers.Order(StringComparer.Ordinal), StringComparer.Ordinal);
   }



   /// <summary>
   /// The blocker this boundary once caused: a job whose options repeat a member the agent reads used to be rejected
   /// before its identifier was read, so it was never answered and the server offered it again forever. It is now
   /// refused exactly once (received, then FAILED with nothing submitted, never downloaded or printed), the lane counts
   /// it as processed, and the next claim takes the job behind it.
   /// </summary>
   [Fact]
   public async Task JobWithARepeatedOptionIsRefusedOnceAndTheNextJobIsClaimed()
   {
      byte[] document = [1, 2, 3];
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.OfferRaw(Claim(ValidJob()).Replace("\"copies\":1", "\"copies\":1,\"Copies\":2", StringComparison.Ordinal)).Offer(FakePrintServer.Job(document, "job-2"), document);

      Assert.True(await harness.RunOnceAsync());
      Assert.Equal(new[] { "RECEIVED", "FAILED" }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal("The claimed job repeats a member the agent reads. No bytes were submitted.", journal.Last("job-1").Message);
      Assert.Equal(new[] { ("job-1", "RECEIVED"), ("job-1", "FAILED") }, server.Events.Select(entry => (entry.JobId, entry.Event.Status)));
      Assert.Equal(0, server.Downloads);
      Assert.Equal(0, printer.Submissions);

      Assert.True(await harness.RunOnceAsync());
      Assert.Equal("SPOOLED", journal.GetState("job-2"));
      Assert.Equal(1, printer.Submissions);
      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(3, server.Claims);
      Assert.Equal(new[] { "RECEIVED", "FAILED" }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal(2, server.Events.Count(entry => string.Equals(entry.JobId, "job-1", StringComparison.Ordinal)));
   }



   /// <summary>
   /// A claimed job with no options, the shape that used to reach the worker and fail on a null, is reported to the server
   /// as received and then FAILED with the reason, shown in history with its title and printer, and never downloaded or
   /// printed. The pass counts as having processed a job, and the next one claims and spools the job behind it, so one
   /// bad job cannot block a device.
   /// </summary>
   [Fact]
   public async Task WorkerReportsARefusedJobAsFailedAndKeepsWorking()
   {
      byte[] document = [1, 2, 3];
      JsonObject malformed = ValidJob();
      malformed.Remove("options");
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.OfferRaw(Claim(malformed)).Offer(FakePrintServer.Job(document, "job-2"), document);

      Assert.True(await harness.RunOnceAsync());
      Assert.Equal(new[] { "RECEIVED", "FAILED" }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal("The claimed job has no print options. No bytes were submitted.", journal.Last("job-1").Message);
      Assert.Equal(new[] { "RECEIVED", "FAILED" }, server.States, StringComparer.Ordinal);
      Assert.Empty(journal.Pending());
      Assert.Equal("Synthetic", journal.Jobs["job-1"].Job.Title);
      Assert.Equal("Queue one", journal.Jobs["job-1"].PrinterName);
      Assert.Equal(0, server.Downloads);
      Assert.Equal(0, printer.Submissions);

      Assert.True(await harness.RunOnceAsync());
      Assert.Equal("SPOOLED", journal.GetState("job-2"));
      Assert.Equal(1, printer.Submissions);
   }



   /// <summary>A lane, which drains until the queue is empty, counts a refused job as processed, so it goes on to claim the job behind it instead of stopping the wave; a pass that then finds the queue empty ends it.</summary>
   [Fact]
   public async Task LaneKeepsDrainingAfterARefusedJob()
   {
      JsonObject malformed = ValidJob();
      malformed["contentLength"] = 0;
      await using var harness = new WorkerHarness();
      harness.Server.OfferRaw(Claim(malformed));
      Assert.True(await harness.RunOnceAsync());
      Assert.Equal("FAILED", harness.Journal.GetState("job-1"));
      Assert.False(await harness.RunOnceAsync());
   }



   /// <summary>
   /// A malformed replay of a job the journal already knows changes nothing, whatever state the job is in: no new
   /// event, no new receipt, and the recorded title and printer are not overwritten by the stand-in's blanks. That
   /// matters most for RECEIVED and SUBMITTING, where another lane is working on the job at that moment and a FAILED
   /// saying "no bytes were submitted" would be false. The replay is logged, with the state and the reason, and the
   /// lane ends its pass because the server would only offer the same job again.
   /// </summary>
   [Theory]
   [InlineData("RECEIVED")]
   [InlineData("SUBMITTING")]
   [InlineData("SPOOLED")]
   [InlineData("SPOOLER_COMPLETED")]
   [InlineData("FAILED")]
   [InlineData("OUTCOME_UNKNOWN")]
   public async Task MalformedReplayOfAJobTheJournalKnowsChangesNothing(string state)
   {
      JsonObject malformed = ValidJob();
      malformed.Remove("options");
      malformed["title"] = "Replayed title";
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.OfferRaw(Claim(malformed));
      journal.TrackJob(FakePrintServer.Job([1], "job-1") with { Title = "Original title" }, "Original printer");
      journal.Record("job-1", new PrintEvent("earlier", state));
      journal.Acknowledge("earlier");
      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(new[] { state }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Empty(journal.Pending());
      Assert.Empty(server.Events);
      Assert.Equal(1, server.Claims);
      Assert.Equal("Original title", journal.Jobs["job-1"].Job.Title);
      Assert.Equal("Original printer", journal.Jobs["job-1"].PrinterName);
      Assert.Equal(0, server.Downloads);
      Assert.Equal(0, printer.Submissions);
      Assert.Equal($"job-1: offered again in a form that breaks the contract while already recorded as {state}; nothing was recorded or reported for it. The claimed job has no print options.", Assert.Single(harness.Log));
   }



   /// <summary>A claimed job that cannot even be identified has nothing to be reported against: the violation reaches the caller, and nothing is recorded, downloaded or printed.</summary>
   [Fact]
   public async Task UnidentifiableJobIsNotRecordedAndTheViolationReachesTheCaller()
   {
      JsonObject malformed = ValidJob();
      malformed["jobId"] = "not a safe identifier";
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.OfferRaw(Claim(malformed));
      await Assert.ThrowsAsync<PrintingProtocolException>(() => harness.RunOnceAsync());
      Assert.Empty(journal.Jobs);
      Assert.Empty(journal.Pending());
      Assert.Empty(server.Events);
      Assert.Equal(0, printer.Submissions);
   }



   /// <summary>When the refusal cannot be delivered because the server does not accept the events, both stay in the journal and are delivered, in order, by the next run; the job is not offered to the printer in between.</summary>
   [Fact]
   public async Task UndeliveredRefusalStaysInTheOutboxAndIsDeliveredLater()
   {
      JsonObject malformed = ValidJob();
      malformed.Remove("options");
      bool down = true;
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.Arriving = (kind, _) => down && string.Equals(kind, "events", StringComparison.Ordinal) ? throw new HttpRequestException("Synthetic refusal.") : Task.CompletedTask;
      server.OfferRaw(Claim(malformed));
      await Assert.ThrowsAsync<HttpRequestException>(() => harness.RunOnceAsync());
      Assert.Equal(2, journal.Pending().Count);
      down = false;
      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(new[] { "RECEIVED", "FAILED" }, server.States, StringComparer.Ordinal);
      Assert.Empty(journal.Pending());
      Assert.Equal(0, printer.Submissions);
   }



   /// <summary>If something else records a final state for a job while its receipt is being acknowledged, the worker does not go on to download or submit it.</summary>
   [Fact]
   public async Task JobFinalizedWhileItsReceiptWasInFlightIsNotSubmitted()
   {
      byte[] document = [1, 2, 3];
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.Offer(FakePrintServer.Job(document), document);
      journal.Recorded = (jobId, printEvent) =>
      {
         if(string.Equals(printEvent.Status, "RECEIVED", StringComparison.Ordinal))
         { journal.Record(jobId, new PrintEvent("finalized-elsewhere", "FAILED", "Cancelled by the operator.")); }
      };
      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(new[] { "RECEIVED", "FAILED" }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal(0, server.Downloads);
      Assert.Equal(0, printer.Submissions);
   }



   /// <summary>
   /// The queued-job list is accepted from a server that offers it, treated as empty from one that does not (404) or
   /// that sends no list, and its display-only members may be missing. Nothing is printed from this list, so fields the
   /// agent does not read, and a display member given twice, do not cost the operator the whole preview.
   /// </summary>
   [Fact]
   public async Task QueuedJobsAreReadLenientlyForDisplay()
   {
      using var server = new ScriptedServer();
      server.Add("{}", HttpStatusCode.NotFound).Add("{}").Add("""{"jobs":null}""")
         .Add("""{"jobs":[{"jobId":"queued-1","printerId":"printer1"},{"jobId":"queued-2","printerId":"printer1","title":"Second","format":"PDF","contentSha256":"unused","contentLength":5,"options":{"copies":2},"createdAt":"2026-01-01T00:00:00Z","status":"QUEUED"}]}""")
         .Add("""{"nextCursor":null,"Jobs":"ignored","jobs":[{"jobId":"queued-3","printerId":"printer1","title":"Third","Title":"Third again","deviceId":"device-1","options":{"copies":1,"Copies":2,"finishing":{"a":{"b":{"c":{"d":{"e":{"f":{"g":{"h":{"i":{"j":{"k":{"l":{"m":{"n":{}}}}}}}}}}}}}}}}}]}""");
      using var api = new PrintApiClient("https://synthetic.invalid", handler: server);
      Assert.Empty(await api.QueuedJobsAsync(CancellationToken.None));
      Assert.Empty(await api.QueuedJobsAsync(CancellationToken.None));
      Assert.Empty(await api.QueuedJobsAsync(CancellationToken.None));
      IReadOnlyList<PrintJob> jobs = await api.QueuedJobsAsync(CancellationToken.None);
      Assert.Equal(new PrintJob("queued-1", "printer1", "", "", "", 0, new PrintOptions(), "", ""), jobs[0]);
      Assert.Equal(new PrintJob("queued-2", "printer1", "Second", "PDF", "unused", 5, new PrintOptions(Copies: 2), "2026-01-01T00:00:00Z", "QUEUED"), jobs[1]);
      Assert.Equal(2, jobs.Count);
      PrintJob tolerated = Assert.Single(await api.QueuedJobsAsync(CancellationToken.None));
      Assert.Equal("queued-3", tolerated.JobId);
      Assert.Equal("printer1", tolerated.PrinterId);
      Assert.All(server.Requests, request => Assert.Equal("/printing/v1/device/jobs/queued", request.Uri.AbsolutePath));
   }



   /// <summary>
   /// The accepting side of the queued-job list's two limits: exactly 200 jobs are all returned, and an entry whose
   /// unknown option nests to exactly the 1,024 levels the response is parsed to is read. The list is only displayed, so
   /// a job nested deeper than a claim may be (65 levels) is still shown until it is claimed and refused. One more of
   /// either limit is refused in the theory below.
   /// </summary>
   [Fact]
   public async Task QueuedListAtItsLimitsIsAccepted()
   {
      using var server = new ScriptedServer();
      server.Add("{\"jobs\":[" + string.Join(",", Enumerable.Range(0, 200).Select(index => $$"""{"jobId":"queued-{{index}}","printerId":"printer1"}""")) + "]}")
         .Add(QueuedWithNestedOption(65)).Add(QueuedWithNestedOption(1024));
      using var api = new PrintApiClient("https://synthetic.invalid", handler: server);
      IReadOnlyList<PrintJob> full = await api.QueuedJobsAsync(CancellationToken.None);
      Assert.Equal(200, full.Count);
      Assert.Equal("queued-199", full[^1].JobId);
      Assert.Equal("queued-1", Assert.Single(await api.QueuedJobsAsync(CancellationToken.None)).JobId);
      Assert.Equal("queued-1", Assert.Single(await api.QueuedJobsAsync(CancellationToken.None)).JobId);
   }



   /// <summary>A queued-job list that breaks the contract (not JSON, nested past the 1,024 levels it is parsed to, two lists, not a list, too long, an entry that is not an object, has no usable identifiers or gives one twice, a member of the wrong type) is a protocol violation, and a server error is still reported as an HTTP failure.</summary>
   [Theory]
   [InlineData("<html></html>", "The queued jobs response is not valid JSON.")]
   [InlineData("[]", "The queued jobs response is not a JSON object.")]
   [InlineData("""{"jobs":{}}""", "The queued jobs response does not hold a list of at most 200 jobs.")]
   [InlineData("too-many", "The queued jobs response does not hold a list of at most 200 jobs.")]
   [InlineData("nested-1025", "The queued jobs response is not valid JSON.")]
   [InlineData("""{"jobs":[null]}""", "A queued job is not a JSON object.")]
   [InlineData("""{"jobs":["queued-1"]}""", "A queued job is not a JSON object.")]
   [InlineData("""{"jobs":[{"printerId":"printer1"}]}""", "A queued job has no usable job or printer identifier.")]
   [InlineData("""{"jobs":[{"jobId":"queued-1","printerId":"a b"}]}""", "A queued job has no usable job or printer identifier.")]
   [InlineData("""{"jobs":[],"jobs":[]}""", "The queued jobs response holds more than one list.")]
   [InlineData("""{"jobs":[{"jobId":"queued-1","JobId":"queued-2","printerId":"printer1"}]}""", "A queued job has no usable job or printer identifier.")]
   [InlineData("""{"jobs":[{"jobId":"queued-1","printerId":"printer1","printerId":"printer2"}]}""", "A queued job has no usable job or printer identifier.")]
   [InlineData("""{"jobs":[{"jobId":"queued-1","printerId":"printer1","contentLength":"five"}]}""", "The queued job does not have the expected fields.")]
   public async Task MalformedQueuedJobListIsAProtocolViolation(string body, string message)
   {
      if(string.Equals(body, "too-many", StringComparison.Ordinal))
      { body = "{\"jobs\":[" + string.Join(",", Enumerable.Range(0, 201).Select(index => $$"""{"jobId":"queued-{{index}}","printerId":"printer1"}""")) + "]}"; }
      if(string.Equals(body, "nested-1025", StringComparison.Ordinal))
      { body = QueuedWithNestedOption(1025); }
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
      ["jobId-201-characters"] = job => job["jobId"] = new string('j', 201),
      ["jobId-space"] = job => job["jobId"] = "job 1",
      ["jobId-letter-outside-ascii"] = job => job["jobId"] = "job" + (char)0xE9,
      ["printerId-missing"] = job => job.Remove("printerId"),
      ["printerId-path"] = job => job["printerId"] = "hostile-marker/..\\x",
      ["printerId-201-characters"] = job => job["printerId"] = new string('p', 201),
      ["printerId-comma"] = job => job["printerId"] = "printer,1",
      ["title-missing"] = job => job.Remove("title"),
      ["title-long"] = job => job["title"] = "hostile-marker" + new string('t', 1024),
      ["title-1025-characters"] = job => job["title"] = new string('t', 1025),
      ["title-number"] = job => job["title"] = 7,
      ["format-missing"] = job => job.Remove("format"),
      ["format-empty"] = job => job["format"] = "",
      ["format-symbols"] = job => job["format"] = "PDF;hostile-marker",
      ["format-33-characters"] = job => job["format"] = new string('F', 33),
      ["format-hyphen"] = job => job["format"] = "PDF-A",
      ["hash-missing"] = job => job.Remove("contentSha256"),
      ["hash-short"] = job => job["contentSha256"] = "00",
      ["hash-63-characters"] = job => job["contentSha256"] = new string('a', 63),
      ["hash-65-characters"] = job => job["contentSha256"] = new string('a', 65),
      ["hash-not-hex"] = job => job["contentSha256"] = new string('a', 63) + "g",
      ["length-missing"] = job => job.Remove("contentLength"),
      ["length-zero"] = job => job["contentLength"] = 0,
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
      ["scale-33-characters"] = job => job["options"]!["scale"] = new string('S', 33),
      ["scale-hyphen"] = job => job["options"]!["scale"] = "ACTUAL-SIZE",
      ["paper-partial"] = job => job["options"]!["paperWidthMm"] = 100,
      ["paper-just-over-2000-millimetres"] = job => job["options"] = new JsonObject { ["paperWidthMm"] = 2000.001, ["paperHeightMm"] = 10 },
      ["paper-zero"] = job => job["options"] = new JsonObject { ["paperWidthMm"] = 0, ["paperHeightMm"] = 10 },
      ["paper-range"] = job => job["options"] = new JsonObject { ["paperWidthMm"] = 3000, ["paperHeightMm"] = 10 },
      ["paper-with-media"] = job => job["options"] = new JsonObject { ["paperWidthMm"] = 100, ["paperHeightMm"] = 150, ["mediaProfile"] = new JsonObject { ["id"] = "default", ["revision"] = Hash } },
   };



   /// <summary>Each limit of a claimed job at its accepting edge, by the name the theory uses: the change that puts one member of an otherwise valid job exactly there.</summary>
   private static readonly Dictionary<string, Action<JsonObject>> AtLimit = new(StringComparer.Ordinal)
   {
      ["jobId-200-characters"] = job => job["jobId"] = new string('j', 200),
      ["jobId-every-allowed-character"] = job => job["jobId"] = "Az09-_.:",
      ["printerId-200-characters"] = job => job["printerId"] = new string('p', 200),
      ["printerId-every-allowed-character"] = job => job["printerId"] = "Az09-_.:",
      ["title-1024-characters"] = job => job["title"] = new string('t', 1024),
      ["format-32-characters"] = job => job["format"] = "Az09_" + new string('F', 27),
      ["format-1-character"] = job => job["format"] = "Z",
      ["length-1"] = job => job["contentLength"] = 1,
      ["length-2147483647"] = job => job["contentLength"] = 2_147_483_647,
      ["copies-32767"] = job => job["options"]!["copies"] = 32_767,
      ["scale-32-characters"] = job => job["options"]!["scale"] = "Az09_" + new string('S', 27),
      ["scale-1-character"] = job => job["options"]!["scale"] = "S",
      ["paper-2000-millimetres"] = job =>
      {
         job["options"]!["paperWidthMm"] = 2000;
         job["options"]!["paperHeightMm"] = 2000;
      },
      ["paper-smallest"] = job =>
      {
         job["options"]!["paperWidthMm"] = 0.001;
         job["options"]!["paperHeightMm"] = 0.001;
      },
      ["createdAt-64-characters"] = job => job["createdAt"] = new string('c', 64),
      ["status-32-characters"] = job => job["status"] = new string('S', 32),
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



   /// <summary>
   /// Fills a hand-written body with the valid job, so a test row shows only what it adds: JOB is the whole job,
   /// JOB-MEMBERS its members without the braces, JOB-MEMBERS-WITHOUT-OPTIONS the same less the options, and HASH a
   /// well-formed SHA-256.
   /// </summary>
   private static string Expand(string body)
   {
      string members = ValidJob().ToJsonString()[1..^1];
      string withoutOptions = members.Replace(""","options":{"copies":1,"scale":"ACTUAL_SIZE"}""", "", StringComparison.Ordinal);
      Assert.NotEqual(members, withoutOptions, StringComparer.Ordinal);
      return body.Replace("JOB-MEMBERS-WITHOUT-OPTIONS", withoutOptions, StringComparison.Ordinal).Replace("JOB-MEMBERS", members, StringComparison.Ordinal)
         .Replace("JOB}", "{" + members + "}}", StringComparison.Ordinal).Replace("JOB,", "{" + members + "},", StringComparison.Ordinal).Replace("HASH", Hash, StringComparison.Ordinal);
   }



   /// <summary>A claim for the valid job with one unknown option whose nesting brings the whole response to exactly the given depth: the response is level one, the job two, its options three.</summary>
   private static string ClaimWithNestedOption(int depth)
      => Claim(ValidJob()).Replace("\"scale\":\"ACTUAL_SIZE\"", "\"scale\":\"ACTUAL_SIZE\",\"finishing\":" + Nested(depth - 3), StringComparison.Ordinal);



   /// <summary>A queued-job list of one job with one unknown option whose nesting brings the whole response to exactly the given depth: the response is level one, the list two, the job three, its options four.</summary>
   private static string QueuedWithNestedOption(int depth)
      => "{\"jobs\":[{\"jobId\":\"queued-1\",\"printerId\":\"printer1\",\"options\":{\"finishing\":" + Nested(depth - 4) + "}}]}";



   /// <summary>A claim for the valid job with its options replaced by the given JSON text.</summary>
   private static string ClaimWithOptions(string options)
      => Claim(ValidJob()).Replace("{\"copies\":1,\"scale\":\"ACTUAL_SIZE\"}", options, StringComparison.Ordinal);



   /// <summary>The valid job's options with one unknown member that nests the given number of arrays, each inside the one before: the fewest characters a level of nesting can cost.</summary>
   private static string OptionsNestingArrays(int arrays) => "{\"copies\":1,\"scale\":\"ACTUAL_SIZE\",\"x\":" + new string('[', arrays) + new string(']', arrays) + "}";



   /// <summary>The given number of JSON objects, each inside the one before.</summary>
   private static string Nested(int levels) => string.Concat(Enumerable.Repeat("{\"deeper\":", levels - 1)) + "{}" + new string('}', levels - 1);



   /// <summary>A device-token API client over the fake server; nothing leaves the process.</summary>
   private static PrintApiClient Api(FakePrintServer server) => new("http://localhost", "synthetic-device-token", server);
}
