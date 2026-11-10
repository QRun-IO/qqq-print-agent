/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Windows;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Reading and writing the envelope of version 2 of the management pipe. Anything can be written to the pipe by any
/// administrator's program, so every way a request's text can be wrong is refused, and told apart from the others,
/// before an operation is looked at. Replies are read leniently where a newer service may have added something and
/// strictly where a reply would otherwise be half a reply.
/// </summary>
public sealed class ManagementEnvelopeTests
{
   /// <summary>The server the example requests name.</summary>
   private const string Server = ManagementSamples.ServerId;



   /// <summary>A well-formed request is read with its version, operation, server and arguments, and written back as the same single line.</summary>
   [Fact]
   public void Request_IsReadAndWrittenAsOneLine()
   {
      string text = "{\"v\":2,\"op\":\"printers.share\",\"server\":\"" + Server + "\",\"args\":{\"printerId\":\"printer-a1\",\"shared\":true}}";

      ManagementRequestParse parse = ManagementJson.ParseRequest(text);

      Assert.True(parse.Succeeded());
      Assert.Equal(ManagementEnvelopeProblem.None, parse.Problem);
      Assert.Null(parse.ErrorCode());
      Assert.False(parse.IsFromVersionOneClient());
      Assert.Equal((2, "printers.share", Server), (parse.Request.V, parse.Request.Op, parse.Request.Server));
      Assert.True(ManagementJson.TryReadArguments(parse.Request, out PrintersShareArgs? arguments));
      Assert.Equal(new PrintersShareArgs("printer-a1", Shared: true), arguments);
      Assert.Equal(text, ManagementJson.Write(parse.Request));
      Assert.DoesNotContain('\n', ManagementJson.Write(parse.Request));
   }



   /// <summary>Text that is missing, blank, cut off, not JSON at all, or JSON with a comment or a trailing comma is refused as not JSON, and answered as an invalid request.</summary>
   [Theory]
   [InlineData(null)]
   [InlineData("")]
   [InlineData("   ")]
   [InlineData("{")]
   [InlineData("{\"v\":2,\"op\":\"snapshot\"")]
   [InlineData("snapshot")]
   [InlineData("{\"v\":2,\"op\":\"snapshot\",}")]
   [InlineData("/* comment */ {\"v\":2,\"op\":\"snapshot\"}")]
   [InlineData("{\"v\":2,\"op\":\"snapshot\"} trailing")]
   public void Request_ThatIsNotJson_IsRefused(string? text)
   {
      AssertRefused(text, ManagementEnvelopeProblem.NotJson, ManagementErrorCode.InvalidRequest);
   }



   /// <summary>JSON whose root is an array, text, a number, a boolean or null is refused as not an object.</summary>
   [Theory]
   [InlineData("[]")]
   [InlineData("[{\"v\":2,\"op\":\"snapshot\"}]")]
   [InlineData("\"snapshot\"")]
   [InlineData("2")]
   [InlineData("true")]
   [InlineData("null")]
   public void Request_WhoseRootIsNotAnObject_IsRefused(string text)
   {
      AssertRefused(text, ManagementEnvelopeProblem.NotAnObject, ManagementErrorCode.InvalidRequest);
   }



   /// <summary>An object without a version is how a version 1 client is recognized, whatever else it holds, so the dispatcher can answer it in the version 1 format. A version 1 request as the old companion writes it is one.</summary>
   [Theory]
   [InlineData("{}")]
   [InlineData("{\"op\":\"snapshot\"}")]
   [InlineData("{\"operation\":\"snapshot\"}")]
   [InlineData("{\"operation\":\"resume-after-update\",\"confirmDuplicateRisk\":false,\"claimLegacyDevice\":false,\"startNewPairing\":false}")]
   [InlineData("{\"V\":2,\"op\":\"snapshot\"}")]
   public void Request_WithoutAVersion_IsFromAVersionOneClient(string text)
   {
      ManagementRequestParse parse = ManagementJson.ParseRequest(text);

      Assert.False(parse.Succeeded());
      Assert.Equal(ManagementEnvelopeProblem.VersionMissing, parse.Problem);
      Assert.True(parse.IsFromVersionOneClient());
      Assert.Equal(ManagementErrorCode.UnsupportedVersion, parse.ErrorCode());
   }



   /// <summary>A version that is another number, not a whole number, or not a number is unsupported. That is decided before the other members are looked at, because a request of another version may rightly have members this version does not know.</summary>
   [Theory]
   [InlineData("{\"v\":1,\"op\":\"snapshot\"}")]
   [InlineData("{\"v\":3,\"op\":\"snapshot\"}")]
   [InlineData("{\"v\":0,\"op\":\"snapshot\"}")]
   [InlineData("{\"v\":-2,\"op\":\"snapshot\"}")]
   [InlineData("{\"v\":2.5,\"op\":\"snapshot\"}")]
   [InlineData("{\"v\":\"2\",\"op\":\"snapshot\"}")]
   [InlineData("{\"v\":null,\"op\":\"snapshot\"}")]
   [InlineData("{\"v\":99999999999,\"op\":\"snapshot\"}")]
   [InlineData("{\"v\":3,\"op\":\"snapshot\",\"somethingNewer\":true}")]
   [InlineData("{\"v\":3,\"operation\":{\"name\":\"snapshot\"}}")]
   public void Request_WithAnotherVersion_IsUnsupported(string text)
   {
      ManagementRequestParse parse = ManagementJson.ParseRequest(text);

      Assert.Equal(ManagementEnvelopeProblem.VersionUnsupported, parse.Problem);
      Assert.False(parse.IsFromVersionOneClient());
      Assert.Equal(ManagementErrorCode.UnsupportedVersion, parse.ErrorCode());
   }



   /// <summary>A version 2 request may hold v, op, server and args and nothing else: an unknown member and a known member in another letter case are refused, because a request the service does not fully understand must not be half-obeyed.</summary>
   [Theory]
   [InlineData("{\"v\":2,\"op\":\"snapshot\",\"extra\":1}")]
   [InlineData("{\"v\":2,\"op\":\"snapshot\",\"Server\":\"" + Server + "\"}")]
   [InlineData("{\"v\":2,\"op\":\"snapshot\",\"Args\":{}}")]
   [InlineData("{\"v\":2,\"op\":\"snapshot\",\"result\":{}}")]
   public void Request_WithAnUnexpectedMember_IsRefused(string text)
   {
      AssertRefused(text, ManagementEnvelopeProblem.UnexpectedMember, ManagementErrorCode.InvalidRequest);
   }



   /// <summary>
   /// A request in which any object has the same member twice is refused whole, and told apart from text that is not
   /// JSON: in the envelope, in the arguments, and in a record nested inside the arguments. Two readers can disagree
   /// about which of two values counts; the second row from the end would otherwise confirm a duplicate print the first
   /// value declined. This is decided before the version is looked at, because nothing can be trusted in such a text.
   /// </summary>
   [Theory]
   [InlineData("{\"v\":2,\"op\":\"snapshot\",\"op\":\"snapshot\"}")]
   [InlineData("{\"v\":2,\"v\":2,\"op\":\"snapshot\"}")]
   [InlineData("{\"v\":2,\"v\":3,\"op\":\"snapshot\"}")]
   [InlineData("{\"v\":2,\"op\":\"trust.get\",\"server\":\"" + Server + "\",\"server\":\"" + Server + "\"}")]
   [InlineData("{\"v\":2,\"op\":\"snapshot\",\"args\":{},\"args\":{}}")]
   [InlineData("{\"v\":2,\"op\":\"jobs.retry\",\"server\":\"" + Server + "\",\"args\":{\"jobId\":\"job-204\",\"confirmDuplicateRisk\":false,\"confirmDuplicateRisk\":true}}")]
   [InlineData("{\"v\":2,\"op\":\"jobs.query\",\"server\":\"" + Server + "\",\"args\":{\"query\":{\"pageSize\":25,\"pageSize\":500}}}")]
   [InlineData("{\"op\":\"snapshot\",\"op\":\"snapshot\"}")]
   public void Request_WithARepeatedMember_IsRefusedAtAnyDepth(string text)
   {
      AssertRefused(text, ManagementEnvelopeProblem.RepeatedMember, ManagementErrorCode.InvalidRequest);
   }



   /// <summary>A request without an operation, or with one that is not text, is refused as having none.</summary>
   [Theory]
   [InlineData("{\"v\":2}")]
   [InlineData("{\"v\":2,\"op\":5}")]
   [InlineData("{\"v\":2,\"op\":null}")]
   [InlineData("{\"v\":2,\"op\":[\"snapshot\"]}")]
   [InlineData("{\"v\":2,\"server\":\"" + Server + "\"}")]
   public void Request_WithoutAnOperation_IsRefused(string text)
   {
      AssertRefused(text, ManagementEnvelopeProblem.OperationMissing, ManagementErrorCode.InvalidRequest);
   }



   /// <summary>An operation whose name is not shaped like one (lower-case letters, optionally one dot and a camel-case word, at most 64 characters) is refused as malformed. It is not looked up, so nothing a client sends as a name is treated as one unless it could be one.</summary>
   [Theory]
   [InlineData("")]
   [InlineData(" ")]
   [InlineData("Snapshot")]
   [InlineData("servers.")]
   [InlineData(".list")]
   [InlineData("servers.List")]
   [InlineData("servers.list.extra")]
   [InlineData("servers list")]
   [InlineData("servers/list")]
   [InlineData("jobs-query")]
   [InlineData("jobs.query2")]
   [InlineData("s\u00E9rvers.list")]
   [InlineData("../../snapshot")]
   [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
   public void Request_WithAMalformedOperationName_IsRefused(string operation)
   {
      AssertRefused(JsonSerializer.Serialize(new { v = 2, op = operation }), ManagementEnvelopeProblem.OperationMalformed, ManagementErrorCode.InvalidRequest);
   }



   /// <summary>A name of the right shape parses even when no such operation exists, at the longest length allowed; saying that it does not exist is the routing check's job.</summary>
   [Theory]
   [InlineData("status")]
   [InlineData("servers.explode")]
   [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
   public void Request_WithAWellShapedUnknownOperation_ParsesAndIsThenUnknown(string operation)
   {
      ManagementRequestParse parse = ManagementJson.ParseRequest(JsonSerializer.Serialize(new { v = 2, op = operation }));

      Assert.True(parse.Succeeded());
      ManagementError? refusal = ManagementOperations.Check(parse.Request, out ManagementOperationInfo? found);
      Assert.Equal(ManagementErrorCode.UnknownOperation, refusal?.Code);
      Assert.Null(found);
      Assert.DoesNotContain(operation, refusal!.Message, StringComparison.Ordinal);
   }



   /// <summary>A server is named by its identifier in the one canonical spelling. Upper case, braces, missing hyphens, a prefix, a name, an empty value and a value that is not text are refused as malformed.</summary>
   [Theory]
   [InlineData("\"3F2B8C1E-5D4A-4F6B-9A7C-0E1D2C3B4A5F\"")]
   [InlineData("\"{3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f}\"")]
   [InlineData("\"3f2b8c1e5d4a4f6b9a7c0e1d2c3b4a5f\"")]
   [InlineData("\" 3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f\"")]
   [InlineData("\"3f2b8c1e\"")]
   [InlineData("\"Warehouse\"")]
   [InlineData("\"\"")]
   [InlineData("17")]
   [InlineData("null")]
   [InlineData("{\"id\":\"3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f\"}")]
   public void Request_WithAMalformedServer_IsRefused(string server)
   {
      AssertRefused("{\"v\":2,\"op\":\"trust.get\",\"server\":" + server + "}", ManagementEnvelopeProblem.ServerMalformed, ManagementErrorCode.InvalidRequest);
   }



   /// <summary>Arguments are an object or absent; an array, text, a number and null are refused.</summary>
   [Theory]
   [InlineData("[]")]
   [InlineData("\"printer-a1\"")]
   [InlineData("5")]
   [InlineData("null")]
   public void Request_WhoseArgumentsAreNotAnObject_IsRefused(string arguments)
   {
      AssertRefused("{\"v\":2,\"op\":\"printers.share\",\"server\":\"" + Server + "\",\"args\":" + arguments + "}", ManagementEnvelopeProblem.ArgumentsNotAnObject, ManagementErrorCode.InvalidRequest);
   }



   /// <summary>A request longer than the frame allows is refused for its size before anything is parsed, whether or not it would have been valid; one of exactly the allowed length is still read.</summary>
   [Fact]
   public void Request_LongerThanTheLimit_IsRefusedUnparsed()
   {
      string valid = "{\"v\":2,\"op\":\"snapshot\"}";
      string atLimit = valid + new string(' ', ManagementProtocol.MaximumRequestCharacters - valid.Length);

      Assert.True(ManagementJson.ParseRequest(atLimit).Succeeded());
      AssertRefused(atLimit + " ", ManagementEnvelopeProblem.TooLarge, ManagementErrorCode.InvalidRequest);
      AssertRefused(new string('{', ManagementProtocol.MaximumRequestCharacters + 1), ManagementEnvelopeProblem.TooLarge, ManagementErrorCode.InvalidRequest);
   }



   /// <summary>The routing check is the one rule for whether a request names a server correctly: an operation on the computer must not name one, and an operation on one server must name it by a full identifier. Each refusal has its code, finds no more than the operation, and never repeats the request.</summary>
   [Theory]
   [InlineData(2, "snapshot", null, null)]
   [InlineData(2, "servers.list", null, null)]
   [InlineData(2, "trust.get", Server, null)]
   [InlineData(2, "jobs.query", Server, null)]
   [InlineData(2, "snapshot", Server, ManagementErrorCode.InvalidRequest)]
   [InlineData(2, "servers.add", Server, ManagementErrorCode.InvalidRequest)]
   [InlineData(2, "update.prepare", Server, ManagementErrorCode.InvalidRequest)]
   [InlineData(2, "trust.get", null, ManagementErrorCode.InvalidRequest)]
   [InlineData(2, "session.signIn", null, ManagementErrorCode.InvalidRequest)]
   [InlineData(2, "servers.remove", "3f2b8c1e", ManagementErrorCode.InvalidRequest)]
   [InlineData(2, "servers.remove", "Warehouse", ManagementErrorCode.InvalidRequest)]
   [InlineData(2, "servers.remove", "3F2B8C1E-5D4A-4F6B-9A7C-0E1D2C3B4A5F", ManagementErrorCode.InvalidRequest)]
   [InlineData(2, "servers.explode", null, ManagementErrorCode.UnknownOperation)]
   [InlineData(2, "Snapshot", null, ManagementErrorCode.UnknownOperation)]
   [InlineData(2, "printers", null, ManagementErrorCode.UnknownOperation)]
   [InlineData(1, "snapshot", null, ManagementErrorCode.UnsupportedVersion)]
   [InlineData(3, "snapshot", null, ManagementErrorCode.UnsupportedVersion)]
   public void RoutingCheck_AcceptsOrRefusesWithTheRightCode(int version, string operation, string? server, string? code)
   {
      ManagementRequest request = new(version, operation, server);

      ManagementError? refusal = ManagementOperations.Check(request, out ManagementOperationInfo? found);

      Assert.Equal(code, refusal?.Code);
      Assert.Equal(code is null or ManagementErrorCode.InvalidRequest, found is not null);
      Assert.True(found is null || string.Equals(found.Name, operation, StringComparison.Ordinal));
      Assert.True(refusal is null || (!refusal.Message.Contains(operation, StringComparison.Ordinal) && (server is null || !refusal.Message.Contains(server, StringComparison.Ordinal))));
      Assert.Null(refusal?.Details);
   }



   /// <summary>The text form of a request names its version, operation and server and never its arguments, which can hold a session credential or an enrollment code; and the argument records that hold one print as fixed text themselves.</summary>
   [Fact]
   public void Request_AndItsSecretArguments_NeverShowTheSecretAsText()
   {
      ServersAddArgs add = new(ManagementSamples.Address, "Warehouse", AcceptCertificateException: false, ManagementSamples.SessionCredential);
      SessionSignInArgs signIn = new(ManagementSamples.SessionCredential);
      PairingEnrollArgs enroll = new(ManagementSamples.EnrollmentCodeCanary);
      var addRequest = ManagementRequest.For(ManagementOperations.ServersAdd, null, add);
      var enrollRequest = ManagementRequest.For(ManagementOperations.PairingEnroll, Server, enroll);
      var plain = ManagementRequest.For(ManagementOperations.Snapshot);

      Assert.Contains(ManagementSamples.SessionCookieCanary, ManagementJson.Write(addRequest), StringComparison.Ordinal);
      Assert.Contains(ManagementSamples.EnrollmentCodeCanary, ManagementJson.Write(enrollRequest), StringComparison.Ordinal);
      Assert.Equal("Management request v2 servers.add (arguments not shown)", addRequest.ToString());
      Assert.Equal("Management request v2 pairing.enroll for server " + Server + " (arguments not shown)", enrollRequest.ToString());
      Assert.Equal("Management request v2 snapshot (arguments not shown)", plain.ToString());
      Assert.Null(plain.Args);
      Assert.Equal("arguments (management payload not shown)", $"arguments {addRequest.Args}");
      Assert.DoesNotContain(ManagementSamples.SessionCookieCanary, $"{addRequest} {addRequest.Args} {enrollRequest} {enrollRequest.Args}", StringComparison.Ordinal);
      Assert.Equal("Arguments of servers.add (session credential not shown)", add.ToString());
      Assert.Equal("Arguments of session.signIn (session credential not shown)", signIn.ToString());
      Assert.Equal("Arguments of pairing.enroll (enrollment code not shown)", enroll.ToString());
   }



   /// <summary>The text form of a reply says whether it succeeded and with which code, and never shows the result, because one result carries the provider's sign-out request; that result and the error print as fixed, safe text as well.</summary>
   [Fact]
   public void Reply_NeverShowsItsResultAsText()
   {
      SignOutResult signedOut = new(ServerSessionEnded: true, UnreadableSessionSetAside: false, ManagementSamples.ProviderLogout);
      var success = ManagementReply.Success(signedOut);
      var failure = ManagementReply.Failure(ManagementErrorCode.Busy, "Another sign-in is running.");
      var detailed = ManagementReply.Failure(ManagementErrorCode.EnrollmentUnconfirmed, "The outcome is not known.", new EnrollmentUnconfirmedDetails(EnrollmentUnconfirmedDetails.Saved, PrintingBlocked: true, "device-17"));

      Assert.Contains(ManagementSamples.IdTokenHintCanary, ManagementJson.Write(success), StringComparison.Ordinal);
      Assert.Equal("Management reply v2 ok (result not shown)", success.ToString());
      Assert.Equal("Sign-out result (provider request not shown)", signedOut.ToString());
      Assert.Equal("Management reply v2 failed: busy", failure.ToString());
      Assert.Equal("busy: Another sign-in is running.", failure.Error!.ToString());
      Assert.Equal("enrollment_unconfirmed: The outcome is not known.", detailed.Error!.ToString());
      Assert.Equal("Management reply v2 failed: no error given", new ManagementReply(2, false).ToString());
      Assert.Equal("(management payload not shown)", success.Result!.ToString());
      Assert.Equal("result (management payload not shown) details (management payload not shown)", $"result {success.Result} details {detailed.Error.Details}");
      Assert.Contains(ManagementSamples.IdTokenHintCanary, success.Result.ToElement().ToString(), StringComparison.Ordinal);
   }



   /// <summary>A successful reply and a failed one are read back as written; the result and the details are then read into their own types.</summary>
   [Fact]
   public void Reply_IsReadBackAsWritten()
   {
      string success = ManagementJson.Write(ManagementReply.Success(new UpdatePrepareResult(Ready: false, JobsInHand: 2, ClaimPending: true)));
      string failure = ManagementJson.Write(ManagementReply.Failure(ManagementErrorCode.EnrollmentUnconfirmed, "The outcome is not known.",
         new EnrollmentUnconfirmedDetails(EnrollmentUnconfirmedDetails.Saved, PrintingBlocked: true, "device-17")));

      ManagementReplyParse succeeded = ManagementJson.ParseReply(success);
      ManagementReplyParse failed = ManagementJson.ParseReply(failure);

      Assert.Equal("{\"v\":2,\"ok\":true,\"result\":{\"ready\":false,\"jobsInHand\":2,\"claimPending\":true}}", success);
      Assert.Equal("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"enrollment_unconfirmed\",\"message\":\"The outcome is not known.\",\"details\":{\"stage\":\"saved\",\"printingBlocked\":true,\"deviceId\":\"device-17\"}}}", failure);
      Assert.True(succeeded.Succeeded());
      Assert.True(ManagementJson.TryReadResult(succeeded.Reply, out UpdatePrepareResult? result));
      Assert.Equal(new UpdatePrepareResult(Ready: false, JobsInHand: 2, ClaimPending: true), result);
      Assert.True(failed.Succeeded());
      Assert.Equal((false, ManagementErrorCode.EnrollmentUnconfirmed, "The outcome is not known."), (failed.Reply.Ok, failed.Reply.Error!.Code, failed.Reply.Error.Message));
      Assert.True(ManagementJson.TryReadDetails(failed.Reply.Error, out EnrollmentUnconfirmedDetails? details));
      Assert.Equal(new EnrollmentUnconfirmedDetails(EnrollmentUnconfirmedDetails.Saved, PrintingBlocked: true, "device-17"), details);
   }



   /// <summary>A member a client does not know is skipped wherever a newer service may add one: in the envelope, in a result, in an error and in details. A code the client does not know is still read, so its message can be shown.</summary>
   [Fact]
   public void Reply_FromANewerService_IsReadWithItsUnknownMembersSkipped()
   {
      ManagementReplyParse success = ManagementJson.ParseReply("{\"v\":2,\"ok\":true,\"result\":{\"ready\":true,\"jobsInHand\":0,\"claimPending\":false,\"addedLater\":{\"deep\":[1,2]}},\"traceId\":\"abc\"}");
      ManagementReplyParse failure = ManagementJson.ParseReply("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"code_added_later\",\"message\":\"Something newer.\",\"hint\":7,\"details\":{\"stage\":\"saved\",\"printingBlocked\":false,\"addedLater\":true}}}");

      Assert.True(success.Succeeded());
      Assert.True(ManagementJson.TryReadResult(success.Reply, out UpdatePrepareResult? result));
      Assert.Equal(new UpdatePrepareResult(Ready: true, JobsInHand: 0, ClaimPending: false), result);
      Assert.True(failure.Succeeded());
      Assert.Equal("code_added_later", failure.Reply.Error!.Code);
      Assert.DoesNotContain("code_added_later", ManagementErrorCode.All, StringComparer.Ordinal);
      Assert.True(ManagementJson.TryReadDetails(failure.Reply.Error, out EnrollmentUnconfirmedDetails? details));
      Assert.Equal(new EnrollmentUnconfirmedDetails(EnrollmentUnconfirmedDetails.Saved, PrintingBlocked: false), details);
   }



   /// <summary>Each way a reply's text can be unusable is told apart: text that is not JSON (which is what a version 1 service answers), a root that is not an object, no version, another version, a reply that lacks what its ok promises, and one longer than the limit.</summary>
   [Theory]
   [InlineData(null, ManagementEnvelopeProblem.NotJson)]
   [InlineData("", ManagementEnvelopeProblem.NotJson)]
   [InlineData("Error: Unknown command.", ManagementEnvelopeProblem.NotJson)]
   [InlineData("Ready for update.", ManagementEnvelopeProblem.NotJson)]
   [InlineData("[]", ManagementEnvelopeProblem.NotAnObject)]
   [InlineData("\"Ready for update.\"", ManagementEnvelopeProblem.NotAnObject)]
   [InlineData("{\"ok\":true,\"result\":{}}", ManagementEnvelopeProblem.VersionMissing)]
   [InlineData("{\"enrolled\":true,\"connected\":true,\"health\":\"GREEN\"}", ManagementEnvelopeProblem.VersionMissing)]
   [InlineData("{\"v\":3,\"ok\":true,\"result\":{}}", ManagementEnvelopeProblem.VersionUnsupported)]
   [InlineData("{\"v\":\"2\",\"ok\":true,\"result\":{}}", ManagementEnvelopeProblem.VersionUnsupported)]
   [InlineData("{\"v\":2}", ManagementEnvelopeProblem.ReplyIncomplete)]
   [InlineData("{\"v\":2,\"result\":{}}", ManagementEnvelopeProblem.ReplyIncomplete)]
   [InlineData("{\"v\":2,\"ok\":\"yes\",\"result\":{}}", ManagementEnvelopeProblem.ReplyIncomplete)]
   [InlineData("{\"v\":2,\"ok\":true}", ManagementEnvelopeProblem.ReplyIncomplete)]
   [InlineData("{\"v\":2,\"ok\":true,\"result\":\"done\"}", ManagementEnvelopeProblem.ReplyIncomplete)]
   [InlineData("{\"v\":2,\"ok\":true,\"error\":{\"code\":\"busy\",\"message\":\"Busy.\"}}", ManagementEnvelopeProblem.ReplyIncomplete)]
   [InlineData("{\"v\":2,\"ok\":false}", ManagementEnvelopeProblem.ReplyIncomplete)]
   [InlineData("{\"v\":2,\"ok\":false,\"result\":{}}", ManagementEnvelopeProblem.ReplyIncomplete)]
   [InlineData("{\"v\":2,\"ok\":false,\"error\":\"busy\"}", ManagementEnvelopeProblem.ReplyIncomplete)]
   [InlineData("{\"v\":2,\"ok\":false,\"error\":{\"message\":\"Busy.\"}}", ManagementEnvelopeProblem.ReplyIncomplete)]
   [InlineData("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"busy\"}}", ManagementEnvelopeProblem.ReplyIncomplete)]
   [InlineData("{\"v\":2,\"ok\":false,\"error\":{\"code\":7,\"message\":\"Busy.\"}}", ManagementEnvelopeProblem.ReplyIncomplete)]
   [InlineData("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"busy\",\"message\":null}}", ManagementEnvelopeProblem.ReplyIncomplete)]
   public void Reply_ThatIsUnusable_IsToldApart(string? text, ManagementEnvelopeProblem problem)
   {
      ManagementReplyParse parse = ManagementJson.ParseReply(text);

      Assert.False(parse.Succeeded());
      Assert.Null(parse.Reply);
      Assert.Equal(problem, parse.Problem);
   }



   /// <summary>A reply longer than the limit is refused for its size without being parsed; details that are not an object are dropped, since every details type is one, and the failure is still read.</summary>
   [Fact]
   public void Reply_SizeLimitAndStrayDetails()
   {
      ManagementReplyParse oversize = ManagementJson.ParseReply(new string(' ', ManagementProtocol.MaximumReplyCharacters + 1));
      ManagementReplyParse strayDetails = ManagementJson.ParseReply("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"busy\",\"message\":\"Busy.\",\"details\":\"text\"}}");

      Assert.Equal(ManagementEnvelopeProblem.TooLarge, oversize.Problem);
      Assert.True(strayDetails.Succeeded());
      Assert.Null(strayDetails.Reply.Error!.Details);
      Assert.False(ManagementJson.TryReadDetails(strayDetails.Reply.Error, out EnrollmentUnconfirmedDetails? details));
      Assert.Null(details);
   }



   /// <summary>Arguments are read strictly: an unknown member, a repeated member, a missing required member, a null where a value is needed, a value of the wrong kind and a member in another letter case are each refused. Arguments that are left out are read as an empty object, which this operation refuses because its members are required.</summary>
   [Theory]
   [InlineData("{\"printerId\":\"printer-a1\",\"shared\":true}", true)]
   [InlineData("{\"shared\":false,\"printerId\":\"printer-a1\"}", true)]
   [InlineData("{\"printerId\":\"printer-a1\",\"shared\":true,\"extra\":1}", false)]
   [InlineData("{\"printerId\":\"printer-a1\",\"shared\":false,\"shared\":true}", false)]
   [InlineData("{\"printerId\":\"other\",\"printerId\":\"printer-a1\",\"shared\":true}", false)]
   [InlineData("{\"printerId\":\"printer-a1\"}", false)]
   [InlineData("{\"shared\":true}", false)]
   [InlineData("{\"printerId\":null,\"shared\":true}", false)]
   [InlineData("{\"printerId\":\"printer-a1\",\"shared\":\"true\"}", false)]
   [InlineData("{\"printerId\":7,\"shared\":true}", false)]
   [InlineData("{\"PrinterId\":\"printer-a1\",\"shared\":true}", false)]
   [InlineData("{}", false)]
   [InlineData(null, false)]
   public void Arguments_AreReadStrictly(string? arguments, bool accepted)
   {
      ManagementRequest request = new(2, ManagementOperations.PrintersShare, Server, arguments is null ? null : Payload(arguments));

      bool read = ManagementJson.TryReadArguments(request, out PrintersShareArgs? shared);

      Assert.Equal(accepted, read);
      Assert.Equal(accepted ? new PrintersShareArgs("printer-a1", arguments!.Contains("\"shared\":true", StringComparison.Ordinal)) : null, shared);
   }



   /// <summary>Nested records are strict too, optional members take their defaults, and a value the type itself refuses (a session token that could not be a cookie, an address that is not one) fails the read instead of throwing, without the refusal quoting the value.</summary>
   [Fact]
   public void Arguments_NestedRecordsAndValuesTheTypeRefuses()
   {
      Assert.True(ManagementJson.TryReadArguments(Request("{\"query\":{}}"), out JobsQueryArgs? defaults));
      Assert.Equal(new JobsQueryArgs(new Qqq.PrintAgent.Windows.JobQuery()), defaults);
      Assert.False(ManagementJson.TryReadArguments(Request("{\"query\":{\"pageSize\":25,\"unknown\":1}}"), out JobsQueryArgs? _));
      Assert.True(ManagementJson.TryReadArguments(Request("{\"jobId\":\"job-204\"}"), out JobsRetryArgs? retry));
      Assert.Equal(new JobsRetryArgs("job-204", ConfirmDuplicateRisk: false), retry);
      Assert.True(ManagementJson.TryReadArguments(Request("{}"), out SessionSignOutArgs? signOut));
      Assert.Equal(new SessionSignOutArgs(), signOut);
      Assert.True(ManagementJson.TryReadArguments(Request("{\"sessionCredential\":{\"origin\":\"https://qqq.example.test\",\"sessionToken\":\"abc\"}}"), out SessionSignInArgs? signIn));
      Assert.Equal((new Uri("https://qqq.example.test"), "abc", null), (signIn.SessionCredential.Origin, signIn.SessionCredential.SessionToken, signIn.Repair));
      Assert.False(ManagementJson.TryReadArguments(Request("{\"sessionCredential\":{\"origin\":\"https://qqq.example.test\",\"sessionToken\":\"has space\"}}"), out SessionSignInArgs? _));
      Assert.False(ManagementJson.TryReadArguments(Request("{\"sessionCredential\":{\"origin\":\"https://qqq.example.test\",\"sessionToken\":\"\"}}"), out SessionSignInArgs? _));
      Assert.False(ManagementJson.TryReadArguments(Request("{\"sessionCredential\":{\"origin\":\"https://qqq.example.test\"}}"), out SessionSignInArgs? _));
      Assert.False(ManagementJson.TryReadArguments(Request("{\"sessionCredential\":{\"origin\":\"https://qqq.example.test\",\"sessionToken\":\"abc\",\"refreshToken\":\"x\"}}"), out SessionSignInArgs? _));
      Assert.False(ManagementJson.TryReadArguments(Request("{\"sessionCredential\":null}"), out SessionSignInArgs? _));
   }



   /// <summary>A result or details that do not fit the type asked for, or are absent, are reported as unreadable, not thrown.</summary>
   [Fact]
   public void ResultAndDetails_ThatDoNotFit_AreNotRead()
   {
      ManagementReply wrongShape = new(2, true, Payload("{\"ready\":\"soon\"}"));
      var failure = ManagementReply.Failure(ManagementErrorCode.Busy, "Busy.");
      ManagementError wrongDetails = new(ManagementErrorCode.EnrollmentUnconfirmed, "Unknown.", Payload("{\"stage\":5}"));

      Assert.False(ManagementJson.TryReadResult(wrongShape, out UpdatePrepareResult? _));
      Assert.False(ManagementJson.TryReadResult(failure, out UpdatePrepareResult? _));
      Assert.False(ManagementJson.TryReadDetails(failure.Error!, out EnrollmentUnconfirmedDetails? _));
      Assert.False(ManagementJson.TryReadDetails(wrongDetails, out EnrollmentUnconfirmedDetails? _));
   }



   /// <summary>Names on the wire are camel case, a member without a value is left out, and a date is written in UTC with a trailing Z whatever offset it had and read back as the same instant. Only ISO 8601 that names its instant is read: a date and time with a Z or an offset. One without either, a date alone, and other notations of a date are refused, as is text that is not a date.</summary>
   [Fact]
   public void Wire_IsCamelCase_OmitsNulls_AndCarriesDatesInUtc()
   {
      UpdateState paused = new(Paused: true, new DateTimeOffset(2026, 11, 10, 17, 4, 5, 123, TimeSpan.FromHours(2)));

      string written = ManagementJson.Write(ManagementReply.Success(paused));
      string withoutValue = ManagementJson.Write(ManagementReply.Success(new UpdateState(Paused: false)));

      Assert.Equal("{\"v\":2,\"ok\":true,\"result\":{\"paused\":true,\"pausedUntil\":\"2026-11-10T15:04:05.1230000Z\"}}", written);
      Assert.Equal("{\"v\":2,\"ok\":true,\"result\":{\"paused\":false}}", withoutValue);
      Assert.True(ManagementJson.TryReadResult(ManagementJson.ParseReply(written).Reply!, out UpdateState? read));
      Assert.Equal(paused.PausedUntil, read.PausedUntil);
      Assert.Equal(TimeSpan.Zero, read.PausedUntil!.Value.Offset);
      Assert.True(ManagementJson.TryReadResult(Result("{\"paused\":true,\"pausedUntil\":\"2026-11-10T17:04:05+02:00\"}"), out UpdateState? withOffset));
      Assert.Equal((new DateTimeOffset(2026, 11, 10, 15, 4, 5, TimeSpan.Zero), TimeSpan.Zero), (withOffset.PausedUntil!.Value, withOffset.PausedUntil.Value.Offset));
      Assert.True(ManagementJson.TryReadResult(Result("{\"paused\":true,\"pausedUntil\":\"2026-11-10T09:34:05.5-05:30\"}"), out UpdateState? negativeOffset));
      Assert.Equal(new DateTimeOffset(2026, 11, 10, 15, 4, 5, 500, TimeSpan.Zero), negativeOffset.PausedUntil);
      Assert.All(new[] { "2026-11-10T15:04:05", "2026-11-10", "11/10/2026", "11/10/2026 15:04:05 +00:00", "Tue, 10 Nov 2026 15:04:05 GMT", "2026-11-10 15:04:05Z", "tomorrow", "", "Z" },
         text => Assert.False(ManagementJson.TryReadResult(Result("{\"paused\":true,\"pausedUntil\":\"" + text + "\"}"), out UpdateState? _)));
      Assert.False(ManagementJson.TryReadResult(Result("{\"paused\":true,\"pausedUntil\":17}"), out UpdateState? _));
   }



   /// <summary>The session credential in the arguments of servers.add is the type Core validates, so a request cannot smuggle in a token that could alter a cookie header.</summary>
   [Fact]
   public void ServersAdd_ReadsItsCredentialThroughCoresValidation()
   {
      var request = ManagementRequest.For(ManagementOperations.ServersAdd, null, new ServersAddArgs(ManagementSamples.Address, "Warehouse", AcceptCertificateException: true, ManagementSamples.SessionCredential));

      Assert.True(ManagementJson.TryReadArguments(request, out ServersAddArgs? arguments));

      Assert.IsType<QqqSessionCredential>(arguments.SessionCredential);
      Assert.Equal((ManagementSamples.Address, "Warehouse", true, ManagementSamples.SessionCookieCanary), (arguments.Address, arguments.Name, arguments.AcceptCertificateException, arguments.SessionCredential.SessionToken));
   }



   /// <summary>
   /// A request without arguments is read as one with empty arguments, so it is valid exactly when every member of the
   /// operation's argument record is optional. Sign-out is the one such operation today, which is why the command line
   /// can send it bare; an operation with a required argument refuses the same request. The update operations take no
   /// arguments at all, and must go on accepting a request without them if they ever gain some, because the update helper
   /// is always one version behind the service.
   /// </summary>
   [Fact]
   public void ArgumentsThatAreLeftOut_AreReadAsAnEmptyObject()
   {
      var signOut = ManagementRequest.For(ManagementOperations.SessionSignOut, Server);
      string written = ManagementJson.Write(signOut);

      Assert.Equal("{\"v\":2,\"op\":\"session.signOut\",\"server\":\"" + Server + "\"}", written);
      Assert.True(ManagementJson.TryReadArguments(ManagementJson.ParseRequest(written).Request!, out SessionSignOutArgs? arguments));
      Assert.Equal(new SessionSignOutArgs(), arguments);
      Assert.False(ManagementJson.TryReadArguments(ManagementRequest.For(ManagementOperations.JobsCancel, Server), out JobsCancelArgs? _));
      Assert.False(ManagementJson.TryReadArguments(ManagementRequest.For(ManagementOperations.PairingEnroll, Server), out PairingEnrollArgs? _));
      Assert.Null(ManagementOperations.All.Single(operation => string.Equals(operation.Name, ManagementOperations.UpdatePrepare, StringComparison.Ordinal)).ArgumentsType);
      Assert.Null(ManagementOperations.All.Single(operation => string.Equals(operation.Name, ManagementOperations.UpdateResume, StringComparison.Ordinal)).ArgumentsType);
   }



   /// <summary>A repeated member is refused by the typed readers too, at the top of the arguments and inside a nested record, so the rule does not depend on the text having come through the envelope parser.</summary>
   [Theory]
   [InlineData("{\"jobId\":\"job-204\",\"confirmDuplicateRisk\":false,\"confirmDuplicateRisk\":true}")]
   [InlineData("{\"jobId\":\"job-1\",\"jobId\":\"job-204\"}")]
   public void Arguments_WithARepeatedMember_AreRefusedByTheTypedReader(string arguments)
   {
      Assert.False(ManagementJson.TryReadArguments(Request(arguments), out JobsRetryArgs? retry));
      Assert.Null(retry);
      Assert.False(ManagementJson.TryReadArguments(Request("{\"query\":{\"pageSize\":25,\"pageSize\":500}}"), out JobsQueryArgs? _));
      Assert.False(ManagementJson.TryReadResult(Result("{\"ready\":true,\"jobsInHand\":0,\"jobsInHand\":5,\"claimPending\":false}"), out UpdatePrepareResult? _));
   }



   /// <summary>A list with a null in it is refused, in arguments and in a result, at any depth; the serializer's own null checks do not reach inside a list.</summary>
   [Fact]
   public void ListsWithANullItem_AreRefused()
   {
      Assert.True(ManagementJson.TryReadArguments(Request("{\"serverIds\":[\"" + Server + "\"]}"), out ServersReorderArgs? order));
      Assert.Equal(Server, Assert.Single(order.ServerIds));
      Assert.False(ManagementJson.TryReadArguments(Request("{\"serverIds\":[null]}"), out ServersReorderArgs? _));
      Assert.False(ManagementJson.TryReadArguments(Request("{\"serverIds\":[\"" + Server + "\",null]}"), out ServersReorderArgs? _));
      Assert.False(ManagementJson.TryReadArguments(Request("{\"serverIds\":null}"), out ServersReorderArgs? _));
      Assert.False(ManagementJson.TryReadResult(Result("{\"servers\":[null]}"), out ServerListResult? _));
      Assert.False(ManagementJson.TryReadResult(Result("{\"stages\":[{\"stage\":\"connection\",\"outcome\":\"failed\",\"problems\":[null]}],\"signInMethods\":[]}"), out ServerCheckResult? _));
      Assert.True(ManagementJson.TryReadResult(Result("{\"servers\":[]}"), out ServerListResult? empty));
      Assert.Empty(empty.Servers);
   }



   /// <summary>A reply that lacks a member its record requires, or has a null where the record needs a value, is an unreadable reply, not a record with a hole in it; an optional member may still be absent, and an unknown one is still skipped.</summary>
   [Fact]
   public void Result_ThatLacksARequiredMember_IsNotRead()
   {
      Assert.False(ManagementJson.TryReadResult(Result("{\"ready\":true}"), out UpdatePrepareResult? _));
      Assert.False(ManagementJson.TryReadResult(Result("{}"), out ServerListResult? _));
      Assert.False(ManagementJson.TryReadResult(Result("{\"servers\":null}"), out ServerListResult? _));
      Assert.False(ManagementJson.TryReadResult(Result("{\"server\":{\"serverId\":\"" + Server + "\",\"name\":null,\"address\":\"https://qqq.example.test\",\"position\":0,\"health\":\"GREEN\",\"status\":\"Connected.\"}}"), out ServerListResult? _));
      Assert.False(ManagementJson.TryReadDetails(new ManagementError(ManagementErrorCode.CancelRefused, "Refused.", Payload("{}")), out CancelRefusedDetails? _));
      Assert.True(ManagementJson.TryReadResult(Result("{\"paused\":false,\"addedLater\":1}"), out UpdateState? state));
      Assert.Equal(new UpdateState(Paused: false), state);
      Assert.True(ManagementJson.TryReadDetails(new ManagementError(ManagementErrorCode.CancelRefused, "Refused.", Payload("{\"reason\":\"reasonAddedLater\"}")), out CancelRefusedDetails? unknownReason));
      Assert.Equal("reasonAddedLater", unknownReason.Reason);
   }



   /// <summary>
   /// A member that may be null is left out of the text when it is null, and its absence reads back as null even where the
   /// record gives it no default: a signed-out status has no principal or tenant, a job that has not reached Windows has
   /// no spooler job or message. A member that may not be null is still required right beside it.
   /// </summary>
   [Fact]
   public void Result_ThatLeavesOutAMemberThatMayBeNull_IsRead()
   {
      AgentAuthenticationStatus signedOut = new(SignedIn: false, PrincipalId: null, TenantId: null, Message: "Sign in to start printing.");
      ManagementReply written = ManagementJson.ParseReply(ManagementJson.Write(ManagementReply.Success(signedOut))).Reply!;

      Assert.DoesNotContain("principalId", ManagementJson.Write(ManagementReply.Success(signedOut)), StringComparison.Ordinal);
      Assert.True(ManagementJson.TryReadResult(written, out AgentAuthenticationStatus? read));
      Assert.Equal(signedOut, read);
      Assert.True(ManagementJson.TryReadResult(Result("{\"serverId\":\"" + Server + "\",\"transport\":{\"protocol\":\"WSS\",\"state\":\"DISCONNECTED\",\"uploadedBytes\":0,\"downloadedBytes\":0,\"uploadBytesPerSecond\":0,\"downloadBytesPerSecond\":0,\"totalsSinceUtc\":\"2026-11-10T09:00:00Z\"}}"), out ServerTransport? neverReached));
      Assert.Null(neverReached.LastContact);
      Assert.False(ManagementJson.TryReadResult(Result("{\"serverId\":\"" + Server + "\"}"), out ServerTransport? _));
      Assert.False(ManagementJson.TryReadResult(Result("{\"signedIn\":false,\"authorized\":false}"), out AgentAuthenticationStatus? _));
   }



   /// <summary>The envelope records can also be read by the serializer itself, which reads a payload as the JSON object it is and refuses anything else; and a payload cannot be made from anything but an object.</summary>
   [Fact]
   public void Payload_IsAJsonObject_AndNothingElse()
   {
      ManagementRequest? read = JsonSerializer.Deserialize<ManagementRequest>("{\"v\":2,\"op\":\"printers.share\",\"server\":\"" + Server + "\",\"args\":{\"printerId\":\"printer-a1\",\"shared\":true}}", ManagementJson.Options);

      Assert.True(ManagementJson.TryReadArguments(read!, out PrintersShareArgs? arguments));
      Assert.Equal(new PrintersShareArgs("printer-a1", Shared: true), arguments);
      Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ManagementRequest>("{\"v\":2,\"op\":\"printers.share\",\"args\":[1]}", ManagementJson.Options));
      Assert.Throws<ArgumentException>(() => ManagementPayload.FromElement(JsonDocument.Parse("[1]").RootElement));
      Assert.Throws<ArgumentException>(() => ManagementPayload.FromElement(JsonDocument.Parse("\"text\"").RootElement));
   }



   /// <summary>Requires a request's text to be refused with one problem and one code, and no request to come back.</summary>
   private static void AssertRefused(string? text, ManagementEnvelopeProblem problem, string code)
   {
      ManagementRequestParse parse = ManagementJson.ParseRequest(text);

      Assert.False(parse.Succeeded());
      Assert.Null(parse.Request);
      Assert.Equal(problem, parse.Problem);
      Assert.Equal(code, parse.ErrorCode());
      Assert.False(parse.IsFromVersionOneClient());
   }



   /// <summary>A request for some operation with the given arguments object; which operation does not matter to reading arguments.</summary>
   private static ManagementRequest Request(string arguments) => new(2, ManagementOperations.Snapshot, null, Payload(arguments));



   /// <summary>A successful reply with the given result object.</summary>
   private static ManagementReply Result(string result) => new(2, true, Payload(result));



   /// <summary>A JSON object as the payload an envelope carries. It is parsed the lenient way on purpose, so a test can hand the typed readers something the envelope parser would have refused.</summary>
   private static ManagementPayload Payload(string json) => ManagementPayload.FromElement(JsonDocument.Parse(json).RootElement);
}
