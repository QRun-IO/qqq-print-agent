/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// The JSON parser accepts, as valid JSON, text it cannot afterwards read: a surrogate escape with no partner, and
/// bytes that are not UTF-8. A claim carrying such text where the agent reads used to end in a failure that named no
/// job, so the job was neither refused nor answered and the server offered it again forever. These tests put every
/// form of such text in every place a claim can carry it and require one of three outcomes and nothing else: the job
/// is refused and still identified, the text is in a field the agent does not read and the job is claimed as if it
/// were not there, or the job's own identifier is the unreadable text and the claim names no job. Then they run the
/// refusals through the worker, which must report the job once and go on to the job behind it. Every body is built
/// from bytes, because several of them are not text.
/// </summary>
public sealed class ClaimUnreadableTextTests
{
   private const string NameRefusal = "The claim has a member name that is not readable text.";
   private const string FieldRefusal = "The claimed job does not have the expected fields.";
   private const string Marker = "<?>";
   private const string ValidClaim = """{"job":{MEMBERS,"options":{OPTIONS}}}""";
   private const string ValidMembers = """
      "jobId":"job-1","printerId":"printer1","title":"Synthetic","format":"ZPL","contentSha256":"HASH","contentLength":3,"createdAt":"2026-01-01T00:00:00Z","status":"CLAIMED"
      """;
   private const string ValidOptions = """
      "copies":1,"scale":"ACTUAL_SIZE"
      """;

   private static readonly string Hash = new('a', 64);
   private static readonly PrintJob Valid = new("job-1", "printer1", "Synthetic", "ZPL", Hash, 3, new PrintOptions(), "2026-01-01T00:00:00Z", "CLAIMED");
   private static readonly PrintJob StandIn = Valid with { ContentSha256 = "", ContentLength = 0 };



   /// <summary>
   /// Each form of text the parser accepts and cannot read, as the bytes that go into a claim. The first three are
   /// surrogate escapes, six ASCII characters for each surrogate, beginning with a backslash; the last two are bytes
   /// that are not UTF-8, the second of them a surrogate written directly as three bytes.
   /// </summary>
   private static readonly Dictionary<string, byte[]> Forms = new(StringComparer.Ordinal)
   {
      ["lone-high-surrogate"] = Encoding.ASCII.GetBytes("""\ud800"""),
      ["lone-low-surrogate"] = Encoding.ASCII.GetBytes("""\udc00"""),
      ["reversed-pair"] = Encoding.ASCII.GetBytes("""\udc00\ud800"""),
      ["byte-that-is-not-utf-8"] = [0xFF],
      ["surrogate-written-as-bytes"] = [0xED, 0xA0, 0x80],
   };



   /// <summary>
   /// Each place where unreadable text makes the agent refuse the job: the claim, with the marker where the text goes,
   /// the reason, and the stand-in the refusal must carry. In a body MEMBERS is the valid job's members without its
   /// options, OPTIONS the valid options and HASH a well-formed SHA-256.
   /// </summary>
   private static readonly Dictionary<string, (string Claim, string Reason, PrintJob StandIn)> Refused = new(StringComparer.Ordinal)
   {
      ["name-beside-the-job"] = ("""{"<?>":1,"job":{MEMBERS,"options":{OPTIONS}}}""", NameRefusal, StandIn),
      ["name-after-the-job"] = ("""{"job":{MEMBERS,"options":{OPTIONS}},"<?>":1}""", NameRefusal, StandIn),
      ["name-in-the-job"] = ("""{"job":{"<?>":1,MEMBERS,"options":{OPTIONS}}}""", NameRefusal, StandIn),
      ["name-in-the-options"] = ("""{"job":{MEMBERS,"options":{"<?>":1,OPTIONS}}}""", NameRefusal, StandIn),
      ["name-in-the-media-selection"] = ("""{"job":{MEMBERS,"options":{OPTIONS,"mediaProfile":{"<?>":1,"id":"default","revision":"HASH"}}}}""", NameRefusal, StandIn),
      ["title"] = (InPlaceOf("Synthetic"), FieldRefusal, StandIn with { Title = "" }),
      ["printer-identifier"] = (InPlaceOf("printer1"), FieldRefusal, StandIn with { PrinterId = "" }),
      ["format"] = (InPlaceOf("ZPL"), FieldRefusal, StandIn with { Format = "" }),
      ["creation-time"] = (InPlaceOf("2026-01-01T00:00:00Z"), FieldRefusal, StandIn with { CreatedAt = "" }),
      ["content-hash"] = (InPlaceOf("HASH"), FieldRefusal, StandIn),
      ["status"] = (InPlaceOf("CLAIMED"), FieldRefusal, StandIn),
      ["scale"] = (InPlaceOf("ACTUAL_SIZE"), FieldRefusal, StandIn),
      ["media-identifier"] = ("""{"job":{MEMBERS,"options":{OPTIONS,"mediaProfile":{"id":"<?>","revision":"HASH"}}}}""", FieldRefusal, StandIn),
      ["media-revision"] = ("""{"job":{MEMBERS,"options":{OPTIONS,"mediaProfile":{"id":"default","revision":"<?>"}}}}""", FieldRefusal, StandIn),
   };



   /// <summary>Each place where unreadable text is in a field the agent does not read, so the job is claimed as if the field were not there.</summary>
   private static readonly Dictionary<string, string> Ignored = new(StringComparer.Ordinal)
   {
      ["value-beside-the-job"] = """{"note":"<?>","job":{MEMBERS,"options":{OPTIONS}}}""",
      ["value-of-an-unknown-job-member"] = """{"job":{"note":"<?>",MEMBERS,"options":{OPTIONS}}}""",
      ["value-of-an-unknown-option"] = """{"job":{MEMBERS,"options":{OPTIONS,"finishing":"<?>"}}}""",
      ["name-inside-an-unknown-option"] = """{"job":{MEMBERS,"options":{OPTIONS,"finishing":{"<?>":1}}}}""",
      ["name-inside-an-unknown-job-member"] = """{"job":{"relayed":[{"<?>":"<?>"}],MEMBERS,"options":{OPTIONS}}}""",
   };



   /// <summary>The cross product the refusal theories run: every place a refusal is expected in, with every form of unreadable text.</summary>
   public static TheoryData<string, string> RefusedPlacesAndForms() => Cross(Refused.Keys);



   /// <summary>The cross product the acceptance theory runs: every place the agent does not read, with every form of unreadable text.</summary>
   public static TheoryData<string, string> IgnoredPlacesAndForms() => Cross(Ignored.Keys);



   /// <summary>Every form of unreadable text, for a theory about one place.</summary>
   public static TheoryData<string> EveryForm() => [.. Forms.Keys];



   /// <summary>
   /// What the rest of this class stands on: each form is valid JSON to the parser, as a string and as a member name,
   /// and asking for it as text fails, and not with the JSON failure the readers catch. If a later JSON library reads
   /// such text leniently this fails first and says the rule needs another look.
   /// </summary>
   [Theory]
   [MemberData(nameof(EveryForm))]
   public void ParserAcceptsEachFormAndCannotReadIt(string form)
   {
      using var document = JsonDocument.Parse(Bytes("""{"<?>":"<?>"}""", form));
      JsonProperty member = Assert.Single(document.RootElement.EnumerateObject());
      Assert.Equal(JsonValueKind.String, member.Value.ValueKind);
      Assert.Throws<InvalidOperationException>(() => member.Name);
      Assert.Throws<InvalidOperationException>(() => member.Value.GetString());
      Assert.Null(ProtocolJson.Name(member));
      Assert.Null(ProtocolJson.Text(member.Value));
      Assert.True(ProtocolJson.HasUnreadableName(document.RootElement));
   }



   /// <summary>
   /// The escapes are the characters their names say, a backslash and all, so nothing that rewrites escapes on the way
   /// into this file can turn a row into readable text without this failing. Text that can be read is still read: a
   /// surrogate pair the right way round, written as escapes, is a name and a value like any other.
   /// </summary>
   [Fact]
   public void SurrogateFormsAreEscapesAndAProperPairIsStillRead()
   {
      Assert.Equal(new byte[] { 0x5C, 0x75, 0x64, 0x38, 0x30, 0x30 }, Forms["lone-high-surrogate"]);
      Assert.Equal(new byte[] { 0x5C, 0x75, 0x64, 0x63, 0x30, 0x30 }, Forms["lone-low-surrogate"]);
      Assert.Equal(new byte[] { 0x5C, 0x75, 0x64, 0x63, 0x30, 0x30, 0x5C, 0x75, 0x64, 0x38, 0x30, 0x30 }, Forms["reversed-pair"]);

      byte[] pair = [.. Forms["lone-high-surrogate"], .. Forms["lone-low-surrogate"]];
      byte[] body = [.. "{\""u8, .. pair, .. "\":\""u8, .. pair, .. "\"}"u8];
      using var document = JsonDocument.Parse(body);
      JsonProperty member = Assert.Single(document.RootElement.EnumerateObject());
      string expected = char.ConvertFromUtf32(0x10000);
      Assert.Equal(expected, ProtocolJson.Name(member));
      Assert.Equal(expected, ProtocolJson.Text(member.Value));
      Assert.False(ProtocolJson.HasUnreadableName(document.RootElement));
   }



   /// <summary>
   /// A claim that names its job and carries unreadable text where the agent reads is one protocol violation with a
   /// fixed reason, and it carries the stand-in: the job's identifier, and every display fact that could be read. A
   /// member name is refused by the agent's own rule, wherever the agent reads members (beside the job, in it, in its
   /// options, in its media selection). A text member is refused because the job cannot be read into its fields, and
   /// the stand-in leaves that one member empty; nothing unreadable is carried anywhere.
   /// </summary>
   [Theory]
   [MemberData(nameof(RefusedPlacesAndForms))]
   public async Task UnreadableTextWhereTheAgentReadsIsRefusedAndTheJobIsStillIdentified(string place, string form)
   {
      (string claim, string reason, PrintJob standIn) = Refused[place];
      using var server = new FakePrintServer();
      server.OfferBytes(Bytes(claim, form));
      using PrintApiClient api = FakeSessionClient.ApiOver(server);
      PrintingProtocolException violation = await Assert.ThrowsAsync<PrintingProtocolException>(() => api.ClaimAsync(CancellationToken.None));
      Assert.Equal(reason, violation.Message);
      Assert.Equal(standIn, violation.RejectedJob);
   }



   /// <summary>
   /// The worker's side of every refusal above. The job is refused exactly once: received, then FAILED with the fixed
   /// reason and the fact that nothing was submitted, both delivered, never downloaded and never handed to a printer.
   /// The pass counts as work done, so the lane claims again and prints the job behind it, and the pass after that
   /// finds the queue empty. Before the fix the first pass threw a failure that named no job and the lane stopped
   /// there, with the job behind it never claimed.
   /// </summary>
   [Theory]
   [MemberData(nameof(RefusedPlacesAndForms))]
   public async Task WorkerRefusesTheJobOnceAndGoesOnToTheJobBehindIt(string place, string form)
   {
      (string claim, string reason, PrintJob standIn) = Refused[place];
      byte[] document = [1, 2, 3];
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.OfferBytes(Bytes(claim, form)).Offer(FakePrintServer.Job(document, "job-2"), document);

      Assert.True(await harness.RunOnceAsync());
      Assert.Equal(new[] { "RECEIVED", "FAILED" }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal(reason + " No bytes were submitted.", journal.Last("job-1").Message);
      Assert.Equal(new[] { ("job-1", "RECEIVED"), ("job-1", "FAILED") }, server.Events.Select(entry => (entry.JobId, entry.Event.Status)));
      Assert.Empty(journal.Pending());
      Assert.Equal(standIn, journal.Jobs["job-1"].Job);
      Assert.Equal(0, server.Downloads);
      Assert.Equal(0, printer.Submissions);

      Assert.True(await harness.RunOnceAsync());
      Assert.Equal("SPOOLED", journal.GetState("job-2"));
      Assert.Equal(1, server.Downloads);
      Assert.Equal(1, printer.Submissions);
      Assert.False(await harness.RunOnceAsync());
      Assert.Equal(3, server.Claims);
      Assert.Equal(new[] { "RECEIVED", "FAILED" }, journal.StatesOf("job-1"), StringComparer.Ordinal);
      Assert.Equal(2, server.Events.Count(entry => string.Equals(entry.JobId, "job-1", StringComparison.Ordinal)));
      Assert.Equal(new[] { "job-1: RECEIVED", "job-1: FAILED" }, harness.Log.Where(line => line.Contains("job-1", StringComparison.Ordinal)), StringComparer.Ordinal);
   }



   /// <summary>
   /// Unreadable text in a field the agent does not read changes nothing, like anything else in such a field: the
   /// value of an unknown member beside the job, in the job or in its options, and names and values inside an unknown
   /// member. The job is claimed exactly as the same claim without that field would be.
   /// </summary>
   [Theory]
   [MemberData(nameof(IgnoredPlacesAndForms))]
   public async Task UnreadableTextInAFieldTheAgentDoesNotReadIsIgnored(string place, string form)
   {
      using var server = new FakePrintServer();
      server.OfferBytes(Bytes(Ignored[place], form));
      using PrintApiClient api = FakeSessionClient.ApiOver(server);
      Assert.Equal(Valid, await api.ClaimAsync(CancellationToken.None));
   }



   /// <summary>
   /// A response that offers no job has nothing in it to refuse, so a member name that cannot be read beside a null
   /// job, or in a response with no job member at all, is passed over and the claim is empty. It used to throw.
   /// </summary>
   [Theory]
   [MemberData(nameof(EveryForm))]
   public async Task UnreadableNameInAResponseThatOffersNoJobIsAnEmptyClaim(string form)
   {
      using var server = new FakePrintServer();
      server.OfferBytes(Bytes("""{"<?>":1,"job":null}""", form)).OfferBytes(Bytes("""{"job":null,"<?>":1}""", form)).OfferBytes(Bytes("""{"<?>":1}""", form));
      using PrintApiClient api = FakeSessionClient.ApiOver(server);
      Assert.Null(await api.ClaimAsync(CancellationToken.None));
      Assert.Null(await api.ClaimAsync(CancellationToken.None));
      Assert.Null(await api.ClaimAsync(CancellationToken.None));
      Assert.Equal(3, server.Claims);
   }



   /// <summary>
   /// The one case that stays unanswered, and it is the documented one: when the job's own identifier is the text that
   /// cannot be read, the claim names no job. The violation carries no stand-in and reaches the caller, nothing is
   /// recorded, reported, downloaded or printed, and the lane stops, as it does for any claim that names no job. It
   /// is the protocol violation, not the JSON library's own failure.
   /// </summary>
   [Theory]
   [MemberData(nameof(EveryForm))]
   public async Task UnreadableJobIdentifierNamesNoJobAndTheViolationReachesTheCaller(string form)
   {
      await using var harness = new WorkerHarness();
      (FakePrintServer server, RecordingJournal journal, FakePrinterBackend printer) = (harness.Server, harness.Journal, harness.Printer);
      server.OfferBytes(Bytes(InPlaceOf("job-1"), form));
      PrintingProtocolException violation = await Assert.ThrowsAsync<PrintingProtocolException>(() => harness.RunOnceAsync());
      Assert.Equal(FieldRefusal, violation.Message);
      Assert.Null(violation.RejectedJob);
      Assert.Empty(journal.Jobs);
      Assert.Empty(journal.Pending());
      Assert.Empty(server.Events);
      Assert.Equal(0, server.Downloads);
      Assert.Equal(0, printer.Submissions);
   }



   /// <summary>
   /// The queued-job list is only displayed, and it goes through the same member lookups, so they must not fail there
   /// either. A member name that cannot be read beside the list is passed over and the list is shown. Inside an entry
   /// the list keeps the behaviour it had: the JSON library skips a name that is not UTF-8, so the entry is shown, and
   /// fails on an unpaired surrogate escape and on unreadable text in a member it reads, so the list is refused as a
   /// protocol violation. None of these is the JSON library's own failure any more.
   /// </summary>
   [Theory]
   [MemberData(nameof(EveryForm))]
   public void QueuedListWithUnreadableTextIsShownOrRefusedAsAProtocolViolation(string form)
   {
      bool escape = Forms[form][0] == 0x5C;
      Assert.Equal("queued-1", Assert.Single(PrintJobContract.ReadQueued(Bytes("""{"<?>":1,"jobs":[{"jobId":"queued-1","printerId":"printer1"}]}""", form))).JobId);
      Assert.Equal("queued-1", Assert.Single(PrintJobContract.ReadQueued(Bytes("""{"JOBS":[{"jobId":"queued-1","printerId":"printer1"}],"<?>":1}""", form))).JobId);
      byte[] nameInEntry = Bytes("""{"jobs":[{"<?>":1,"jobId":"queued-1","printerId":"printer1"}]}""", form);
      if(escape)
      { Assert.Equal("The queued job does not have the expected fields.", Assert.Throws<PrintingProtocolException>(() => PrintJobContract.ReadQueued(nameInEntry)).Message); }
      else
      { Assert.Equal("queued-1", Assert.Single(PrintJobContract.ReadQueued(nameInEntry)).JobId); }
      byte[] title = Bytes("""{"jobs":[{"jobId":"queued-1","printerId":"printer1","title":"<?>"}]}""", form);
      Assert.Equal("The queued job does not have the expected fields.", Assert.Throws<PrintingProtocolException>(() => PrintJobContract.ReadQueued(title)).Message);
   }



   /// <summary>The valid claim with the marker in place of one text value, which must occur in it exactly once, so the unreadable text becomes the whole of that member.</summary>
   private static string InPlaceOf(string value)
   {
      string quoted = '"' + value + '"';
      string claim = Expand(ValidClaim);
      Assert.Equal(2, claim.Split(quoted).Length);
      return claim.Replace(quoted, '"' + Marker + '"', StringComparison.Ordinal);
   }



   /// <summary>Fills a body with the valid job: MEMBERS is its members without its options, OPTIONS its options. HASH is left for <see cref="Bytes"/>.</summary>
   private static string Expand(string body)
      => body.Replace("MEMBERS", ValidMembers, StringComparison.Ordinal).Replace("OPTIONS", ValidOptions, StringComparison.Ordinal);



   /// <summary>The body as bytes: the text as UTF-8, filled with the valid job and a well-formed hash, with the form's bytes at every marker, of which there must be at least one.</summary>
   private static byte[] Bytes(string body, string form)
   {
      string[] parts = Expand(body).Replace("HASH", Hash, StringComparison.Ordinal).Split(Marker);
      Assert.True(parts.Length > 1, "The body has no place for the unreadable text.");
      var bytes = new List<byte>(Encoding.UTF8.GetBytes(parts[0]));
      foreach(string part in parts.Skip(1))
      {
         bytes.AddRange(Forms[form]);
         bytes.AddRange(Encoding.UTF8.GetBytes(part));
      }
      return [.. bytes];
   }



   /// <summary>Every given place with every form.</summary>
   private static TheoryData<string, string> Cross(IEnumerable<string> places)
   {
      var rows = new TheoryData<string, string>();
      foreach(string place in places)
      {
         foreach(string form in Forms.Keys)
         { rows.Add(place, form); }
      }
      return rows;
   }
}
