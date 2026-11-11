/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using System.Text.Json.Serialization;
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



   /// <summary>
   /// JSON's grammar lets a string hold the escape of a surrogate without its partner, which is not text and cannot
   /// be read into a string. A request that holds one is refused like any other request that is wrong in that place,
   /// and reading it never throws: in the operation it is a malformed operation, in the server a malformed server,
   /// and in a member's name, in the envelope or at any depth beneath it, the text is not JSON, because the parse
   /// has to read every name to know that none is repeated. A high surrogate and a low one are the same in this, and
   /// so is one that follows text that would have been valid. The texts are plain ASCII: the escape is six characters.
   /// </summary>
   [Theory]
   [InlineData("{\"v\":2,\"op\":\"\\ud800\"}", ManagementEnvelopeProblem.OperationMalformed)]
   [InlineData("{\"v\":2,\"op\":\"\\udc00\"}", ManagementEnvelopeProblem.OperationMalformed)]
   [InlineData("{\"v\":2,\"op\":\"snapshot\\ud800\"}", ManagementEnvelopeProblem.OperationMalformed)]
   [InlineData("{\"v\":2,\"op\":\"\\ud800\",\"server\":\"" + Server + "\"}", ManagementEnvelopeProblem.OperationMalformed)]
   [InlineData("{\"v\":2,\"op\":\"snapshot\",\"server\":\"\\ud800\"}", ManagementEnvelopeProblem.ServerMalformed)]
   [InlineData("{\"v\":2,\"op\":\"trust.get\",\"server\":\"" + Server + "\\udc00\"}", ManagementEnvelopeProblem.ServerMalformed)]
   [InlineData("{\"v\":2,\"op\":\"snapshot\",\"\\ud800\":1}", ManagementEnvelopeProblem.NotJson)]
   [InlineData("{\"v\":2,\"op\":\"snapshot\",\"args\":{\"\\ud800\":1}}", ManagementEnvelopeProblem.NotJson)]
   [InlineData("{\"v\":2,\"op\":\"jobs.query\",\"server\":\"" + Server + "\",\"args\":{\"query\":{\"page\\udc00Size\":25}}}", ManagementEnvelopeProblem.NotJson)]
   [InlineData("{\"\\ud800\":2,\"op\":\"snapshot\"}", ManagementEnvelopeProblem.NotJson)]
   public void Request_ThatHoldsASurrogateWithoutItsPartner_IsRefusedAndNeverThrows(string text, ManagementEnvelopeProblem problem)
   {
      AssertRefused(text, problem, ManagementErrorCode.InvalidRequest);
   }



   /// <summary>
   /// The same for a string that holds the surrogate itself, which only code can build (text read from the pipe as
   /// UTF-8 cannot hold one): it cannot be carried as UTF-8, so a request and a reply that hold one are refused as
   /// not JSON, wherever it stands, and reading them never throws.
   /// </summary>
   [Fact]
   public void Text_ThatHoldsASurrogateItself_IsRefusedAsNotJson()
   {
      string lone = ((char)0xD800).ToString();

      AssertRefused("{\"v\":2,\"op\":\"snap" + lone + "shot\"}", ManagementEnvelopeProblem.NotJson, ManagementErrorCode.InvalidRequest);
      AssertRefused("{\"v\":2,\"op\":\"snapshot\"}" + lone, ManagementEnvelopeProblem.NotJson, ManagementErrorCode.InvalidRequest);
      AssertRefused("{\"v\":2,\"op\":\"snapshot\",\"op\":\"snap" + lone + "shot\"}", ManagementEnvelopeProblem.NotJson, ManagementErrorCode.InvalidRequest);
      Assert.Equal(ManagementEnvelopeProblem.NotJson, ManagementJson.ParseReply("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"busy\",\"message\":\"a" + lone + "b\"}}").Problem);
      Assert.Equal(ManagementEnvelopeProblem.NotJson, ManagementJson.ParseReply("{\"v\":2,\"ok\":true,\"result\":{\"reason\":\"a" + lone + "b\"}}").Problem);
   }



   /// <summary>
   /// The escape of a surrogate without its partner in a value inside the arguments, a result or the details leaves
   /// the envelope readable, and the record that holds it is then not read: arguments are refused, and a result and
   /// details do not fit their type. Writing such a payload back fails with the serializer's own error for text it
   /// cannot write, as it did when the serializer wrote the envelope, and not with a stray error of the writer. A
   /// surrogate pair written as two escapes is text, and is read.
   /// </summary>
   [Fact]
   public void Payload_ThatHoldsASurrogateWithoutItsPartner_IsNotReadAndIsNotWritten()
   {
      ManagementRequestParse request = ManagementJson.ParseRequest("{\"v\":2,\"op\":\"servers.rename\",\"server\":\"" + Server + "\",\"args\":{\"name\":\"a\\ud800\"}}");
      ManagementReplyParse result = ManagementJson.ParseReply("{\"v\":2,\"ok\":true,\"result\":{\"reason\":\"a\\ud800b\"}}");
      ManagementReplyParse details = ManagementJson.ParseReply("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"cancel_refused\",\"message\":\"Refused.\",\"details\":{\"reason\":\"\\udc00\"}}}");
      ManagementReplyParse pair = ManagementJson.ParseReply("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"busy\",\"message\":\"Busy \\ud83d\\ude00.\"}}");

      Assert.True(request.Succeeded());
      Assert.False(ManagementJson.TryReadArguments(request.Request, out ServersRenameArgs? arguments));
      Assert.Null(arguments);
      Assert.True(result.Succeeded());
      Assert.False(ManagementJson.TryReadResult(result.Reply, out CancelRefusedDetails? read));
      Assert.Null(read);
      Assert.True(details.Succeeded());
      Assert.False(ManagementJson.TryReadDetails(details.Reply.Error!, out CancelRefusedDetails? refused));
      Assert.Null(refused);
      Assert.Throws<JsonException>(() => ManagementJson.Write(request.Request));
      Assert.Throws<JsonException>(() => ManagementJson.Write(result.Reply));
      Assert.Throws<JsonException>(() => ManagementJson.Write(details.Reply));
      Assert.True(pair.Succeeded());
      Assert.Equal("Busy \U0001F600.", pair.Reply.Error!.Message);
   }



   /// <summary>
   /// Neither parsing function throws, whatever stands where a string or a name can stand. Each text below has one
   /// place marked, in the envelope of a request or of a reply or beneath it, and each fragment is put there in turn:
   /// the escape of a high or a low surrogate without its partner, two of them, one in the wrong order, one followed
   /// by another character, the surrogate itself, an escape that is cut short or is not one, a NUL written as an
   /// escape and as itself, a line break, a whole surrogate pair, and nothing. Every text gets an answer from both
   /// functions, a request or a reply or the reason there is none, and what was read can be put through the routing
   /// check and the typed readers without an exception either. This is a table, not a fuzzer: it holds the places and
   /// the shapes that are known, and audit R2 still stands for the rest.
   /// </summary>
   [Fact]
   public void ParsingFunctions_NeverThrow_WhateverStandsWhereAStringOrANameCanStand()
   {
      string[] fragments =
      [
         "\\ud800", "\\udc00", "\\ud800\\ud800", "\\udc00\\ud800", "\\udbff\\u0041", "\\ud83d\\ude00", "\\u0000", "\\uffff",
         "\\u12", "\\x", "\\", "\\\\ud800", ((char)0xD800).ToString(), ((char)0xDC00).ToString(), "\0", "\n", "",
      ];
      string[] templates =
      [
         "{\"v\":2,\"op\":\"@\"}",
         "{\"v\":2,\"op\":\"snapshot@\"}",
         "{\"v\":2,\"op\":\"trust.get\",\"server\":\"@\"}",
         "{\"v\":2,\"op\":\"trust.get\",\"server\":\"" + Server + "@\"}",
         "{\"v\":2,\"op\":\"snapshot\",\"@\":1}",
         "{\"@\":2,\"op\":\"snapshot\"}",
         "{\"v\":2,\"op\":\"servers.rename\",\"server\":\"" + Server + "\",\"args\":{\"name\":\"Ware@house\"}}",
         "{\"v\":2,\"op\":\"servers.rename\",\"server\":\"" + Server + "\",\"args\":{\"na@me\":\"Warehouse\"}}",
         "{\"v\":2,\"op\":\"jobs.query\",\"server\":\"" + Server + "\",\"args\":{\"query\":{\"@\":25}}}",
         "{\"v\":2,\"ok\":false,\"error\":{\"code\":\"@\",\"message\":\"Busy.\"}}",
         "{\"v\":2,\"ok\":false,\"error\":{\"code\":\"busy\",\"message\":\"Busy@.\"}}",
         "{\"v\":2,\"ok\":false,\"error\":{\"code\":\"busy\",\"message\":\"Busy.\",\"@\":1}}",
         "{\"v\":2,\"ok\":false,\"error\":{\"code\":\"cancel_refused\",\"message\":\"Refused.\",\"details\":{\"reason\":\"@\"}}}",
         "{\"v\":2,\"ok\":false,\"error\":{\"code\":\"cancel_refused\",\"message\":\"Refused.\",\"details\":{\"@\":\"documentGone\"}}}",
         "{\"v\":2,\"ok\":true,\"result\":{\"reason\":\"@\"}}",
         "{\"v\":2,\"ok\":true,\"result\":{\"@\":true}}",
         "{\"v\":2,\"ok\":true,\"result\":{},\"@\":1}",
         "{\"v\":2,\"ok\":true,\"result\":{},\"error\":{\"code\":\"@\",\"message\":\"@\"}}",
         "\"@\"",
         "@",
      ];
      int requests = 0;
      int replies = 0;

      foreach(string text in templates.SelectMany(template => fragments.Select(fragment => template.Replace("@", fragment, StringComparison.Ordinal))))
      {
         ManagementRequestParse request = ManagementJson.ParseRequest(text);
         ManagementReplyParse reply = ManagementJson.ParseReply(text);

         Assert.Equal(request.Problem == ManagementEnvelopeProblem.None, request.Succeeded());
         Assert.Equal(reply.Problem == ManagementEnvelopeProblem.None, reply.Succeeded());
         if(request.Succeeded())
         {
            requests++;
            ManagementOperations.Check(request.Request, out _);
            ManagementJson.TryReadArguments(request.Request, out ServersRenameArgs? _);
            ManagementJson.TryReadArguments(request.Request, out JobsQueryArgs? _);
         }
         if(reply.Succeeded())
         {
            replies++;
            ManagementJson.TryReadResult(reply.Reply, out CancelRefusedDetails? _);
            Assert.True(reply.Reply.Ok || !ManagementJson.TryReadDetails(reply.Reply.Error!, out CancelRefusedDetails? details) || details.Reason.Length >= 0);
         }
      }

      Assert.Equal(templates.Length * fragments.Length, templates.Length * fragments.Distinct(StringComparer.Ordinal).Count());
      Assert.InRange(requests, 1, templates.Length * fragments.Length);
      Assert.InRange(replies, 1, templates.Length * fragments.Length);
   }



   /// <summary>
   /// A request is built only by the functions that check it, which the parser uses too, so no request exists that
   /// the parser would not read back. Text that is not shaped like an operation's name, and a server named by
   /// anything but a full identifier in its one spelling (a prefix, a name, another letter case, braces, nothing at
   /// all between the quotes), are refused by each of the three functions, and the refusal does not repeat what was
   /// given. A request has no public constructor and no member that can be set, so there is no other way to make
   /// one: not with another version, and not by changing one that was checked.
   /// </summary>
   [Theory]
   [InlineData("Snapshot", null)]
   [InlineData("servers.List", null)]
   [InlineData("servers list", null)]
   [InlineData("Not An Operation\n", null)]
   [InlineData("", null)]
   [InlineData("servers.remove", "3f2b8c1e")]
   [InlineData("servers.remove", "Warehouse")]
   [InlineData("servers.remove", "3F2B8C1E-5D4A-4F6B-9A7C-0E1D2C3B4A5F")]
   [InlineData("servers.remove", "{3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f}")]
   [InlineData("servers.remove", "")]
   public void Request_IsBuiltOnlyByFunctionsThatCheckTheOperationAndTheServer(string operation, string? server)
   {
      ManagementPayload arguments = Payload("{\"printerId\":\"printer-a1\",\"shared\":true}");
      Func<ManagementRequest>[] ways =
      [
         () => ManagementRequest.For(operation, server),
         () => ManagementRequest.For(operation, server, new PrintersShareArgs("printer-a1", Shared: true)),
         () => ManagementRequest.For(operation, server, arguments),
      ];

      Assert.All(ways, way =>
      {
         ArgumentException refusal = Assert.Throws<ArgumentException>(way);
         Assert.True(operation.Length == 0 || !refusal.Message.Contains(operation, StringComparison.Ordinal));
         Assert.True(string.IsNullOrEmpty(server) || !refusal.Message.Contains(server, StringComparison.Ordinal));
      });
      Assert.Throws<ArgumentNullException>(() => ManagementRequest.For(null!));
      Assert.Empty(typeof(ManagementRequest).GetConstructors());
      Assert.All(typeof(ManagementRequest).GetProperties(), member => Assert.Null(member.SetMethod));
      var built = ManagementRequest.For(ManagementOperations.PrintersShare, Server, arguments);
      Assert.Equal((ManagementProtocol.Version, ManagementOperations.PrintersShare, Server), (built.V, built.Op, built.Server));
      Assert.Same(arguments, built.Args);
      Assert.Equal(ManagementJson.Write(built), ManagementJson.Write(ManagementJson.ParseRequest(ManagementJson.Write(built)).Request!));
   }



   /// <summary>
   /// A reply is built only by the functions for a success and for a failure, which the parser uses too. So a reply
   /// is always of this contract's version and holds exactly one of a result and an error: there is no public
   /// constructor to give it another version, an error beside a result, or neither, and no member can be set
   /// afterwards. A success needs its result and a failure its error.
   /// </summary>
   [Fact]
   public void Reply_IsBuiltOnlyByFunctionsThatGiveItExactlyOneOfAResultAndAnError()
   {
      ManagementPayload result = Payload("{\"ready\":true}");
      var success = ManagementReply.Success(result);
      var typed = ManagementReply.Success(new NoResult());
      var failure = ManagementReply.Failure(ManagementErrorCode.Busy, "Busy.");

      Assert.Equal((ManagementProtocol.Version, true), (success.V, success.Ok));
      Assert.Same(result, success.Result);
      Assert.Null(success.Error);
      Assert.Equal("{\"v\":2,\"ok\":true,\"result\":{\"ready\":true}}", ManagementJson.Write(success));
      Assert.Equal("{\"v\":2,\"ok\":true,\"result\":{}}", ManagementJson.Write(typed));
      Assert.Equal((ManagementProtocol.Version, false), (failure.V, failure.Ok));
      Assert.Null(failure.Result);
      Assert.NotNull(failure.Error);
      Assert.Throws<ArgumentNullException>(() => ManagementReply.Success(null!));
      Assert.Throws<ArgumentNullException>(() => ManagementReply.Failure(null!));
      Assert.Empty(typeof(ManagementReply).GetConstructors());
      Assert.All(typeof(ManagementReply).GetProperties(), member => Assert.Null(member.SetMethod));
   }



   /// <summary>
   /// An error comes to exist in three ways and no others: the constructor and <c>WithDetails</c>, which check the
   /// code against the table, and the parser's way in, which takes the error object as the JSON a reply carried and
   /// nothing else. No function of the type, public or internal, turns a code and a message given as text into an
   /// error without those checks, so code in the service's own assembly cannot send "Not A Code" or details for a
   /// code that carries none by reaching past the two that check.
   /// </summary>
   [Fact]
   public void Error_HasNoWayInFromLooseText_BesideTheTwoThatCheckTheCode()
   {
      System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
      string[] makers = [.. typeof(ManagementError).GetMethods(all)
         .Where(method => !method.IsPrivate && (Nullable.GetUnderlyingType(method.ReturnType) ?? method.ReturnType) == typeof(ManagementError))
         .Select(method => method.Name + "(" + string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name)) + ")")
         .Order(StringComparer.Ordinal)];

      Assert.Equal(["FromWire(JsonElement)", "WithDetails(String, String, TDetails)"], makers);
      Assert.Equal("(String, String)", "(" + string.Join(", ", Assert.Single(typeof(ManagementError).GetConstructors()).GetParameters().Select(parameter => parameter.ParameterType.Name)) + ")");
      Assert.Null(ManagementError.FromWire(JsonDocument.Parse("\"busy\"").RootElement));
      Assert.Null(ManagementError.FromWire(JsonDocument.Parse("{\"code\":\"busy\"}").RootElement));
      Assert.Equal("code_added_later", ManagementError.FromWire(JsonDocument.Parse("{\"code\":\"code_added_later\",\"message\":\"Something newer.\"}").RootElement)!.Code);
   }



   /// <summary>The routing check is the one rule for whether a request names a server correctly: an operation on the computer must not name one, and an operation on one server must name one. Each refusal has its code, finds no more than the operation, and never repeats the request. A request of another version, with a name that is not shaped like an operation's, or with a server named by anything but a full identifier, cannot be built, so the check is never asked about one.</summary>
   [Theory]
   [InlineData("snapshot", null, null)]
   [InlineData("servers.list", null, null)]
   [InlineData("trust.get", Server, null)]
   [InlineData("jobs.query", Server, null)]
   [InlineData("snapshot", Server, ManagementErrorCode.InvalidRequest)]
   [InlineData("servers.add", Server, ManagementErrorCode.InvalidRequest)]
   [InlineData("update.prepare", Server, ManagementErrorCode.InvalidRequest)]
   [InlineData("trust.get", null, ManagementErrorCode.InvalidRequest)]
   [InlineData("session.signIn", null, ManagementErrorCode.InvalidRequest)]
   [InlineData("servers.remove", null, ManagementErrorCode.InvalidRequest)]
   [InlineData("servers.explode", null, ManagementErrorCode.UnknownOperation)]
   [InlineData("servers.explode", Server, ManagementErrorCode.UnknownOperation)]
   [InlineData("printers", null, ManagementErrorCode.UnknownOperation)]
   public void RoutingCheck_AcceptsOrRefusesWithTheRightCode(string operation, string? server, string? code)
   {
      var request = ManagementRequest.For(operation, server);

      ManagementError? refusal = ManagementOperations.Check(request, out ManagementOperationInfo? found);

      Assert.Equal(code, refusal?.Code);
      Assert.Equal(code is null or ManagementErrorCode.InvalidRequest, found is not null);
      Assert.True(found is null || string.Equals(found.Name, operation, StringComparison.Ordinal));
      Assert.True(refusal is null || (!refusal.Message.Contains(operation, StringComparison.Ordinal) && (server is null || !refusal.Message.Contains(server, StringComparison.Ordinal))));
      Assert.Null(refusal?.Details);
   }



   /// <summary>
   /// An operation that takes no arguments refuses a request that gives it some, instead of ignoring what the client
   /// meant by them; an empty object says the same as leaving the arguments out and is accepted. An operation that
   /// does take arguments is not judged here: its arguments are read strictly into its own record. The refusal never
   /// repeats what the arguments held.
   /// </summary>
   [Theory]
   [InlineData("update.resume", null, "{\"anything\":1}", ManagementErrorCode.InvalidRequest)]
   [InlineData("snapshot", null, "{\"servers\":[]}", ManagementErrorCode.InvalidRequest)]
   [InlineData("printers.discover", null, "{\"anything\":null}", ManagementErrorCode.InvalidRequest)]
   [InlineData("servers.remove", Server, "{\"anything\":true}", ManagementErrorCode.InvalidRequest)]
   [InlineData("update.resume", null, "{}", null)]
   [InlineData("servers.remove", Server, "{}", null)]
   [InlineData("session.signOut", Server, "{}", null)]
   [InlineData("printers.share", Server, "{\"printerId\":\"printer-a1\",\"shared\":true}", null)]
   public void RoutingCheck_RefusesArgumentsForAnOperationThatTakesNone(string operation, string? server, string arguments, string? code)
   {
      ManagementRequestParse parse = ManagementJson.ParseRequest("{\"v\":2,\"op\":\"" + operation + "\"" + (server is null ? "" : ",\"server\":\"" + server + "\"") + ",\"args\":" + arguments + "}");

      Assert.True(parse.Succeeded());
      ManagementError? refusal = ManagementOperations.Check(parse.Request, out ManagementOperationInfo? found);

      Assert.Equal(code, refusal?.Code);
      Assert.Equal(operation, found?.Name);
      Assert.True(refusal is null || !refusal.Message.Contains("anything", StringComparison.Ordinal));
      Assert.Null(ManagementOperations.Check(ManagementRequest.For(operation, server), out _));
   }



   /// <summary>The text form of a request names its version, operation and server and never its arguments, which can hold a session credential or an enrollment code; and the argument records that hold one print as fixed text themselves.</summary>
   [Fact]
   public void Request_AndItsSecretArguments_NeverShowTheSecretAsText()
   {
      ServersAddArgs add = new(ManagementSamples.Address, "Warehouse", acceptCertificateException: false, ManagementSamples.SessionCredential);
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



   /// <summary>
   /// Each way a reply's text can be unusable is told apart: text that is not JSON (which is what a version 1 service
   /// answers), a root that is not an object, no version, another version, a reply that lacks what its ok promises,
   /// one longer than the limit, and one in which any object has the same member twice (in the envelope, in the
   /// result, in the error or in its details, or under a name written with an escape), which is refused whole as a
   /// request is. The last rows hold the escape of a surrogate without its partner, which is not text: as an error's
   /// code or message it leaves the reply without one, so the reply is incomplete (and not "not JSON", which a
   /// client takes for a version 1 service); in a member's name, at any depth, the text is not JSON. A success whose
   /// stray error holds one is still a success, since that error is dropped. None of them makes the read throw.
   /// </summary>
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
   [InlineData("{\"v\":2,\"ok\":true,\"ok\":true,\"result\":{}}", ManagementEnvelopeProblem.RepeatedMember)]
   [InlineData("{\"v\":2,\"v\":3,\"ok\":true,\"result\":{}}", ManagementEnvelopeProblem.RepeatedMember)]
   [InlineData("{\"v\":2,\"ok\":true,\"result\":{\"ready\":true,\"jobsInHand\":0,\"jobsInHand\":5,\"claimPending\":false}}", ManagementEnvelopeProblem.RepeatedMember)]
   [InlineData("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"busy\",\"code\":\"internal_error\",\"message\":\"Busy.\"}}", ManagementEnvelopeProblem.RepeatedMember)]
   [InlineData("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"cancel_refused\",\"message\":\"Refused.\",\"details\":{\"reason\":\"documentGone\",\"reason\":\"ownershipMismatch\"}}}", ManagementEnvelopeProblem.RepeatedMember)]
   [InlineData("{\"v\":2,\"ok\":true,\"\\u006fk\":false,\"result\":{}}", ManagementEnvelopeProblem.RepeatedMember)]
   [InlineData("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"busy\",\"message\":\"a\\ud800b\"}}", ManagementEnvelopeProblem.ReplyIncomplete)]
   [InlineData("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"busy\",\"message\":\"\\udc00\"}}", ManagementEnvelopeProblem.ReplyIncomplete)]
   [InlineData("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"\\ud800\",\"message\":\"Busy.\"}}", ManagementEnvelopeProblem.ReplyIncomplete)]
   [InlineData("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"busy\\ud800\",\"message\":\"Busy.\"}}", ManagementEnvelopeProblem.ReplyIncomplete)]
   [InlineData("{\"v\":2,\"ok\":true,\"result\":{},\"\\ud800\":1}", ManagementEnvelopeProblem.NotJson)]
   [InlineData("{\"v\":2,\"ok\":true,\"result\":{\"\\ud800\":1}}", ManagementEnvelopeProblem.NotJson)]
   [InlineData("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"busy\",\"message\":\"Busy.\",\"details\":{\"rea\\udc00son\":\"x\"}}}", ManagementEnvelopeProblem.NotJson)]
   public void Reply_ThatIsUnusable_IsToldApart(string? text, ManagementEnvelopeProblem problem)
   {
      ManagementReplyParse parse = ManagementJson.ParseReply(text);

      Assert.False(parse.Succeeded());
      Assert.Null(parse.Reply);
      Assert.Equal(problem, parse.Problem);
   }



   /// <summary>A reply longer than the limit is refused for its size without being parsed; details that are not an object are dropped, since every details type is one, and the failure is still read; and a success that also carries an error is read as the success, whatever that error holds, a code and a message that are not text included.</summary>
   [Fact]
   public void Reply_SizeLimitAndStrayDetails()
   {
      ManagementReplyParse oversize = ManagementJson.ParseReply(new string(' ', ManagementProtocol.MaximumReplyCharacters + 1));
      ManagementReplyParse strayDetails = ManagementJson.ParseReply("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"busy\",\"message\":\"Busy.\",\"details\":\"text\"}}");

      ManagementReplyParse strayError = ManagementJson.ParseReply("{\"v\":2,\"ok\":true,\"result\":{},\"error\":{\"code\":\"\\ud800\",\"message\":\"\\ud800\"}}");

      Assert.Equal(ManagementEnvelopeProblem.TooLarge, oversize.Problem);
      Assert.True(strayError.Succeeded());
      Assert.True(strayError.Reply.Ok);
      Assert.Null(strayError.Reply.Error);
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
      var request = ManagementRequest.For(ManagementOperations.PrintersShare, Server, arguments is null ? null : Payload(arguments));

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
      var wrongShape = ManagementReply.Success(Payload("{\"ready\":\"soon\"}"));
      var failure = ManagementReply.Failure(ManagementErrorCode.Busy, "Busy.");
      ManagementError wrongDetails = ErrorRead(ManagementErrorCode.EnrollmentUnconfirmed, "{\"stage\":5}");

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
      var request = ManagementRequest.For(ManagementOperations.ServersAdd, null, new ServersAddArgs(ManagementSamples.Address, "Warehouse", acceptCertificateException: true, ManagementSamples.SessionCredential));

      Assert.True(ManagementJson.TryReadArguments(request, out ServersAddArgs? arguments));

      Assert.IsType<QqqSessionCredential>(arguments.SessionCredential);
      Assert.Equal((ManagementSamples.Address, "Warehouse", true, ManagementSamples.SessionCookieCanary), (arguments.Address, arguments.Name, arguments.AcceptCertificateException, arguments.SessionCredential.SessionToken));
   }



   /// <summary>
   /// The arguments of servers.add carry the sign-in provider's origin that the check reported, after the credential,
   /// for comparison only: the service still reads the provider itself. A request that leaves the origin out, as the
   /// wizard does when the check named no provider, is read, and states exactly that. A difference between what the
   /// request states and what the service reads is answered with a code of its own that carries no details, and
   /// nothing is stored. The rule for the comparison is held row by row in the test below.
   /// </summary>
   [Fact]
   public void ServersAdd_CarriesTheProviderTheCheckReported_ForComparisonOnly()
   {
      ServersAddArgs named = new(ManagementSamples.Address, "Warehouse", acceptCertificateException: true, ManagementSamples.SessionCredential, ManagementSamples.ProviderOrigin);
      ServersAddArgs unnamed = new(ManagementSamples.Address, "Warehouse", acceptCertificateException: false, ManagementSamples.SessionCredential);
      string written = ManagementJson.Write(ManagementRequest.For(ManagementOperations.ServersAdd, null, named));

      Assert.EndsWith(",\"sessionCredential\":{\"origin\":\"https://qqq.example.test\",\"sessionToken\":\"" + ManagementSamples.SessionCookieCanary + "\"},\"providerOrigin\":\"https://sign-in.example.test\"}}", written, StringComparison.Ordinal);
      Assert.DoesNotContain("providerOrigin", ManagementJson.Write(ManagementRequest.For(ManagementOperations.ServersAdd, null, unnamed)), StringComparison.Ordinal);
      Assert.True(ManagementJson.TryReadArguments(ManagementJson.ParseRequest(written).Request!, out ServersAddArgs? read));
      Assert.Equal((ManagementSamples.ProviderOrigin, "Warehouse", true), (read.ProviderOrigin, read.Name, read.AcceptCertificateException));
      Assert.True(ManagementJson.TryReadArguments(ManagementRequest.For(ManagementOperations.ServersAdd, null, unnamed), out ServersAddArgs? readUnnamed));
      Assert.Null(readUnnamed.ProviderOrigin);
      Assert.True(named.ProviderAgrees("https://sign-in.example.test"));
      Assert.False(named.ProviderAgrees("https://other.example.test"));
      Assert.False(named.ProviderAgrees(null));
      Assert.True(unnamed.ProviderAgrees(null));
      Assert.False(unnamed.ProviderAgrees("https://sign-in.example.test"));
      Assert.Equal("provider_changed", ManagementErrorCode.ProviderChanged);
      Assert.Contains(ManagementErrorCode.ProviderChanged, ManagementErrorCode.All, StringComparer.Ordinal);
      Assert.Equal("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"provider_changed\",\"message\":\"The server now names another sign-in provider. Check the server again.\"}}",
         ManagementJson.Write(ManagementReply.Failure(ManagementErrorCode.ProviderChanged, "The server now names another sign-in provider. Check the server again.")));
      Assert.Throws<ArgumentException>(() => ManagementReply.Failure(ManagementErrorCode.ProviderChanged, "Synthetic message.", ServerCheckResult.From(new QqqServerCompatibility([], null, QqqSignInMethods.None))));
      Assert.Equal("Arguments of servers.add (session credential not shown)", named.ToString());
      Assert.DoesNotContain(ManagementSamples.ProviderOrigin, $"{named} {ManagementRequest.For(ManagementOperations.ServersAdd, null, named)}", StringComparison.Ordinal);
   }



   /// <summary>
   /// trust.set has the same race as servers.add, so its arguments carry the provider's origin the check reported in the
   /// same way: last on the wire, left out to state that the check named none, compared by the same rule and refused
   /// with the same code. A request that switches the exception off accepts nothing, so it is written without the
   /// member and agrees whatever the service reads; a request that switches it on without an origin states that the
   /// check named no provider, and does not agree with a service that reads one.
   /// </summary>
   [Fact]
   public void TrustSet_CarriesTheProviderTheCheckReported_AndComparesItWhenItSwitchesTheExceptionOn()
   {
      TrustSetArgs named = new(AcceptCertificateException: true, ManagementSamples.ProviderOrigin);
      TrustSetArgs off = new(AcceptCertificateException: false);
      string written = ManagementJson.Write(ManagementRequest.For(ManagementOperations.TrustSet, Server, named));

      Assert.Equal("{\"v\":2,\"op\":\"trust.set\",\"server\":\"" + Server + "\",\"args\":{\"acceptCertificateException\":true,\"providerOrigin\":\"https://sign-in.example.test\"}}", written);
      Assert.Equal("{\"v\":2,\"op\":\"trust.set\",\"server\":\"" + Server + "\",\"args\":{\"acceptCertificateException\":false}}", ManagementJson.Write(ManagementRequest.For(ManagementOperations.TrustSet, Server, off)));
      Assert.True(ManagementJson.TryReadArguments(ManagementJson.ParseRequest(written).Request!, out TrustSetArgs? read));
      Assert.Equal(named, read);
      Assert.True(ManagementJson.TryReadArguments(ManagementJson.ParseRequest("{\"v\":2,\"op\":\"trust.set\",\"server\":\"" + Server + "\",\"args\":{\"acceptCertificateException\":true}}").Request!, out TrustSetArgs? withoutOrigin));
      Assert.Equal(new TrustSetArgs(AcceptCertificateException: true), withoutOrigin);
      Assert.False(ManagementJson.TryReadArguments(ManagementJson.ParseRequest("{\"v\":2,\"op\":\"trust.set\",\"server\":\"" + Server + "\",\"args\":{\"providerOrigin\":\"https://sign-in.example.test\"}}").Request!, out TrustSetArgs? _));
      Assert.True(named.ProviderAgrees("https://sign-in.example.test"));
      Assert.False(named.ProviderAgrees("https://other.example.test"));
      Assert.False(named.ProviderAgrees(null));
      Assert.True(withoutOrigin.ProviderAgrees(null));
      Assert.False(withoutOrigin.ProviderAgrees("https://sign-in.example.test"));
      Assert.True(off.ProviderAgrees(null));
      Assert.True(off.ProviderAgrees("https://sign-in.example.test"));
      Assert.True(new TrustSetArgs(AcceptCertificateException: false, "https://other.example.test").ProviderAgrees("https://sign-in.example.test"));
   }



   /// <summary>
   /// The one rule for the provider a request says the operator was shown, for servers.add and for trust.set when it
   /// switches the exception on. The origin states what the check showed, and an absent one states that it showed no
   /// provider: none shown and none read agree; none shown and one read do not, because that provider is exactly the
   /// one the operator has not seen; one shown and none read do not; one shown and another read (another host, port
   /// or scheme) do not; and the same one agrees. Origins are compared in the one form an origin is kept in, so one
   /// place written in another letter case, with its default port, with a trailing slash, with a path, or by its
   /// Unicode name is the same place. What the service reads is its own, and may name nothing or be an IPv6 literal
   /// with a zone: that agrees with no origin a request can carry. What a request carries always names an origin or
   /// is absent; the test below holds what happens to one that does not.
   /// </summary>
   [Theory]
   [InlineData(null, null, true)]
   [InlineData(null, "https://sign-in.example.test", false)]
   [InlineData("https://sign-in.example.test", null, false)]
   [InlineData("https://sign-in.example.test", "https://other.example.test", false)]
   [InlineData("https://sign-in.example.test", "https://sign-in.example.test:8443", false)]
   [InlineData("https://sign-in.example.test", "http://sign-in.example.test", false)]
   [InlineData("https://sign-in.example.test", "https://sign-in.example.test.other.test", false)]
   [InlineData("https://sign-in.example.test", "https://sign-in.example.test", true)]
   [InlineData("https://sign-in.example.test", "https://sign-in.example.test/", true)]
   [InlineData("https://sign-in.example.test", "https://SIGN-IN.Example.Test:443/", true)]
   [InlineData("https://sign-in.example.test/realms/dev", "https://sign-in.example.test", true)]
   [InlineData("https://xn--bcher-kva.example", "https://b\u00FCcher.example/", true)]
   [InlineData("https://sign-in.example.test", "", false)]
   [InlineData("https://sign-in.example.test", "sign-in.example.test", false)]
   [InlineData("https://sign-in.example.test", "not an origin", false)]
   [InlineData(null, "not an origin", false)]
   [InlineData("https://[fe80::1]", "https://[fe80::1]:443/", true)]
   [InlineData("https://[fe80::1]", "https://[fe80::1%25eth0]", false)]
   public void TheProviderTheOperatorWasShown_IsComparedWithTheOneTheServiceRead(string? shown, string? read, bool agrees)
   {
      ServersAddArgs add = new(ManagementSamples.Address, "Warehouse", acceptCertificateException: true, ManagementSamples.SessionCredential, shown);
      TrustSetArgs on = new(AcceptCertificateException: true, shown);

      Assert.Equal(agrees, SignInProviderOrigin.Agrees(shown, read));
      Assert.Equal(agrees, add.ProviderAgrees(read));
      Assert.Equal(agrees, on.ProviderAgrees(read));
      Assert.Equal(agrees, SignInProviderOrigin.Agrees(read, shown));
   }



   /// <summary>
   /// A provider origin that names nothing is refused where the arguments are read, as a name that breaks the name
   /// rule is, and is not read only to be answered "the provider changed" for ever after: empty or blank text, a word
   /// such as "none", a host with no scheme, a scheme that is not a web one, a host that cannot be named, and an IPv6
   /// literal with a zone, which the form origins are compared in has no place for (two zones of one address, or a
   /// zone and none, would compare as one place). Neither argument record can be built with one, in code or from a
   /// request, for trust.set whichever way the switch goes; the refusal does not repeat the text; and by the rule
   /// itself such text agrees with nothing, not even with itself.
   /// </summary>
   [Theory]
   [InlineData("")]
   [InlineData("   ")]
   [InlineData("none")]
   [InlineData("null")]
   [InlineData("not an origin")]
   [InlineData("sign-in.example.test")]
   [InlineData("//sign-in.example.test")]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback")]
   [InlineData("ftp://sign-in.example.test")]
   [InlineData("https://id\u2028.example.test")]
   [InlineData("https://[fe80::1%25eth0]")]
   [InlineData("https://[fe80::1%eth0]:8443")]
   public void AProviderOriginThatNamesNothing_IsRefusedWhenTheArgumentsAreRead(string text)
   {
      string member = ",\"providerOrigin\":" + JsonSerializer.Serialize(text);
      string add = "{\"v\":2,\"op\":\"servers.add\",\"args\":{\"address\":\"https://qqq.example.test\",\"name\":\"Warehouse\",\"acceptCertificateException\":true,"
         + "\"sessionCredential\":{\"origin\":\"https://qqq.example.test\",\"sessionToken\":\"synthetic-session\"}";
      string trust = "{\"v\":2,\"op\":\"trust.set\",\"server\":\"" + Server + "\",\"args\":{\"acceptCertificateException\":";

      ArgumentException refusedAdd = Assert.Throws<ArgumentException>(() => new ServersAddArgs(ManagementSamples.Address, "Warehouse", acceptCertificateException: true, ManagementSamples.SessionCredential, text));
      ArgumentException refusedOn = Assert.Throws<ArgumentException>(() => new TrustSetArgs(AcceptCertificateException: true, text));
      ArgumentException refusedOff = Assert.Throws<ArgumentException>(() => new TrustSetArgs(AcceptCertificateException: false, text));

      Assert.All(new[] { refusedAdd, refusedOn, refusedOff }, refusal => Assert.True(string.IsNullOrWhiteSpace(text) || !refusal.Message.Contains(text, StringComparison.Ordinal)));
      Assert.True(ManagementJson.TryReadArguments(ManagementJson.ParseRequest(add + "}}").Request!, out ServersAddArgs? _));
      Assert.False(ManagementJson.TryReadArguments(ManagementJson.ParseRequest(add + member + "}}").Request!, out ServersAddArgs? _));
      Assert.True(ManagementJson.TryReadArguments(ManagementJson.ParseRequest(trust + "true}}").Request!, out TrustSetArgs? _));
      Assert.False(ManagementJson.TryReadArguments(ManagementJson.ParseRequest(trust + "true" + member + "}}").Request!, out TrustSetArgs? _));
      Assert.False(ManagementJson.TryReadArguments(ManagementJson.ParseRequest(trust + "false" + member + "}}").Request!, out TrustSetArgs? _));
      Assert.False(SignInProviderOrigin.Agrees(text, text));
      Assert.False(SignInProviderOrigin.Agrees(text, null));
      Assert.False(SignInProviderOrigin.Agrees(null, text));
      Assert.False(SignInProviderOrigin.Agrees(text, "https://sign-in.example.test"));
   }



   /// <summary>
   /// "The check named no provider" is said in two ways on the wire, and both are read as the same statement: the
   /// member left out, and the member given as null. Either agrees with a service that reads no provider and with no
   /// other, for servers.add and for trust.set. Written back, the member is left out.
   /// </summary>
   [Theory]
   [InlineData("")]
   [InlineData(",\"providerOrigin\":null")]
   public void AProviderOriginLeftOutOrNull_StatesThatTheCheckNamedNoProvider(string member)
   {
      ManagementRequest add = ManagementJson.ParseRequest("{\"v\":2,\"op\":\"servers.add\",\"args\":{\"address\":\"https://qqq.example.test\",\"name\":\"Warehouse\",\"acceptCertificateException\":true,"
         + "\"sessionCredential\":{\"origin\":\"https://qqq.example.test\",\"sessionToken\":\"synthetic-session\"}" + member + "}}").Request!;
      ManagementRequest trust = ManagementJson.ParseRequest("{\"v\":2,\"op\":\"trust.set\",\"server\":\"" + Server + "\",\"args\":{\"acceptCertificateException\":true" + member + "}}").Request!;

      Assert.True(ManagementJson.TryReadArguments(add, out ServersAddArgs? readAdd));
      Assert.True(ManagementJson.TryReadArguments(trust, out TrustSetArgs? readTrust));

      Assert.Null(readAdd.ProviderOrigin);
      Assert.Null(readTrust.ProviderOrigin);
      Assert.True(readAdd.ProviderAgrees(null));
      Assert.True(readTrust.ProviderAgrees(null));
      Assert.False(readAdd.ProviderAgrees("https://sign-in.example.test"));
      Assert.False(readTrust.ProviderAgrees("https://sign-in.example.test"));
      Assert.DoesNotContain("providerOrigin", ManagementJson.Write(ManagementRequest.For(ManagementOperations.ServersAdd, null, readAdd)), StringComparison.Ordinal);
      Assert.Equal("{\"v\":2,\"op\":\"trust.set\",\"server\":\"" + Server + "\",\"args\":{\"acceptCertificateException\":true}}", ManagementJson.Write(ManagementRequest.For(ManagementOperations.TrustSet, Server, readTrust)));
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



   /// <summary>
   /// A list of the record's with a null in it is refused, in arguments and in a result, at any depth; the serializer's
   /// own null checks do not reach inside a list. Only the lists the record knows are looked at: a member a later
   /// version adds is skipped whole, at the top and inside a nested record, even when it is a list with a null in it,
   /// or today's reader would bind every later version.
   /// </summary>
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
      Assert.True(ManagementJson.TryReadResult(Result("{\"paused\":false,\"addedLater\":[null]}"), out UpdateState? later));
      Assert.Equal(new UpdateState(Paused: false), later);
      Assert.True(ManagementJson.TryReadResult(Result("{\"servers\":[],\"addedLater\":{\"items\":[null,[null]],\"servers\":[null]}}"), out ServerListResult? _));
      Assert.True(ManagementJson.TryReadResult(Result("{\"stages\":[{\"stage\":\"connection\",\"outcome\":\"failed\",\"problems\":[],\"addedLater\":[null]}],\"signInMethods\":[]}"), out ServerCheckResult? nested));
      Assert.Empty(Assert.Single(nested.Stages).Problems);
      Assert.False(ManagementJson.TryReadArguments(Request("{\"serverIds\":[],\"addedLater\":[null]}"), out ServersReorderArgs? _));
   }



   /// <summary>
   /// The rule against a null inside a list holds wherever a list can stand in a record, not only directly in a member
   /// or inside another record: in the values of a dictionary, where a null value is refused as a null item is, and
   /// inside a value type that may be null. No record of the contract has either today, so the records here are the
   /// test's own. A dictionary and a value type without a null in them are read, and a value type that is null or
   /// absent is still allowed where the member may be null.
   /// </summary>
   [Fact]
   public void ListsWithANullItem_AreRefusedInsideADictionaryAndInsideANullableValueType()
   {
      Assert.True(ManagementJson.TryReadResult(Result("{\"lists\":{\"a\":[\"x\"],\"b\":[]}}"), out DictionaryRecord? lists));
      Assert.Equal(["x"], lists.Lists["a"]);
      Assert.False(ManagementJson.TryReadResult(Result("{\"lists\":{\"a\":[null]}}"), out DictionaryRecord? _));
      Assert.False(ManagementJson.TryReadResult(Result("{\"lists\":{\"a\":[\"x\"],\"b\":[\"y\",null]}}"), out DictionaryRecord? _));
      Assert.False(ManagementJson.TryReadResult(Result("{\"lists\":{\"a\":null}}"), out DictionaryRecord? _));
      Assert.False(ManagementJson.TryReadArguments(Request("{\"lists\":{\"a\":[null]}}"), out DictionaryRecord? _));
      Assert.True(ManagementJson.TryReadResult(Result("{\"maybe\":{\"items\":[\"x\"]}}"), out NullableValueRecord? held));
      Assert.Equal(["x"], held.Maybe!.Value.Items);
      Assert.False(ManagementJson.TryReadResult(Result("{\"maybe\":{\"items\":[null]}}"), out NullableValueRecord? _));
      Assert.False(ManagementJson.TryReadResult(Result("{\"maybe\":{\"items\":[\"x\",null]}}"), out NullableValueRecord? _));
      Assert.False(ManagementJson.TryReadArguments(Request("{\"maybe\":{\"items\":[null]}}"), out NullableValueRecord? _));
      Assert.True(ManagementJson.TryReadResult(Result("{\"maybe\":null}"), out NullableValueRecord? none));
      Assert.Null(none.Maybe);
      Assert.True(ManagementJson.TryReadResult(Result("{}"), out NullableValueRecord? absent));
      Assert.Null(absent.Maybe);
   }



   /// <summary>
   /// A payload is a JSON object however it is made. A value that is not written as one (a list, an array, text) is
   /// refused when a reply or a request is built from it, instead of producing a reply its client reports as
   /// incomplete; a record is still written.
   /// </summary>
   [Fact]
   public void Payload_BuiltFromAValueThatIsNotAnObject_IsRefused()
   {
      Assert.Throws<ArgumentException>(() => ManagementReply.Success(new List<string> { "a" }));
      Assert.Throws<ArgumentException>(() => ManagementReply.Success(new[] { 1, 2 }));
      Assert.Throws<ArgumentException>(() => ManagementPayload.From("text"));
      Assert.Throws<ArgumentException>(() => ManagementRequest.For(ManagementOperations.ServersReorder, null, new[] { Server }));
      Assert.Throws<ArgumentException>(() => ManagementPayload.From<NoResult>(null!));
      Assert.True(ManagementPayload.From(new NoResult()).IsEmpty());
      Assert.Equal("{\"v\":2,\"ok\":true,\"result\":{}}", ManagementJson.Write(ManagementReply.Success(new NoResult())));
   }



   /// <summary>A reply that lacks a member its record requires, or has a null where the record needs a value, is an unreadable reply, not a record with a hole in it; an optional member may still be absent, and an unknown one is still skipped.</summary>
   [Fact]
   public void Result_ThatLacksARequiredMember_IsNotRead()
   {
      Assert.False(ManagementJson.TryReadResult(Result("{\"ready\":true}"), out UpdatePrepareResult? _));
      Assert.False(ManagementJson.TryReadResult(Result("{}"), out ServerListResult? _));
      Assert.False(ManagementJson.TryReadResult(Result("{\"servers\":null}"), out ServerListResult? _));
      Assert.False(ManagementJson.TryReadResult(Result("{\"server\":{\"serverId\":\"" + Server + "\",\"name\":null,\"address\":\"https://qqq.example.test\",\"position\":0,\"health\":\"GREEN\",\"status\":\"Connected.\"}}"), out ServerListResult? _));
      Assert.False(ManagementJson.TryReadDetails(ErrorRead(ManagementErrorCode.CancelRefused, "{}"), out CancelRefusedDetails? _));
      Assert.True(ManagementJson.TryReadResult(Result("{\"paused\":false,\"addedLater\":1}"), out UpdateState? state));
      Assert.Equal(new UpdateState(Paused: false), state);
      Assert.True(ManagementJson.TryReadDetails(ErrorRead(ManagementErrorCode.CancelRefused, "{\"reason\":\"reasonAddedLater\"}"), out CancelRefusedDetails? unknownReason));
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



   /// <summary>
   /// The serializer is not a way around the envelope checks. Handed a request directly it would accept another version,
   /// a malformed operation name, a member the envelope does not have and a repeated argument, so the envelope types
   /// refuse to be read by it, whatever the text and with either set of the contract's options, and the refusal names
   /// the functions that read them with their checks. A typed reader asked for an envelope type reads nothing.
   /// </summary>
   [Theory]
   [InlineData("{\"v\":2,\"op\":\"snapshot\"}")]
   [InlineData("{\"v\":7,\"op\":\"snapshot\"}")]
   [InlineData("{\"v\":2,\"op\":\"Not An Operation\"}")]
   [InlineData("{\"v\":2,\"op\":\"snapshot\",\"extra\":1}")]
   [InlineData("{\"v\":2,\"op\":\"jobs.retry\",\"args\":{\"jobId\":\"a\",\"jobId\":\"b\"}}")]
   public void Envelope_IsNotReadByTheSerializer(string text)
   {
      NotSupportedException request = Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<ManagementRequest>(text, ManagementJson.Options));
      NotSupportedException strict = Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<ManagementRequest>(text, ManagementJson.StrictOptions));
      NotSupportedException reply = Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<ManagementReply>("{\"v\":2,\"ok\":true,\"result\":{}}", ManagementJson.Options));
      NotSupportedException error = Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<ManagementError>("{\"code\":\"busy\",\"message\":\"Busy.\"}", ManagementJson.Options));

      Assert.Contains("ManagementJson.ParseRequest", request.Message, StringComparison.Ordinal);
      Assert.Contains("ManagementJson.ParseRequest", strict.Message, StringComparison.Ordinal);
      Assert.Contains("ManagementJson.ParseReply", reply.Message, StringComparison.Ordinal);
      Assert.Contains("ManagementJson.ParseReply", error.Message, StringComparison.Ordinal);
      Assert.False(ManagementJson.TryReadResult(Result(text), out ManagementRequest? _));
      Assert.False(ManagementJson.TryReadArguments(Request("{\"v\":2,\"ok\":true,\"result\":{}}"), out ManagementReply? _));
   }



   /// <summary>
   /// The refusal is declared on the four envelope types, not on the contract's two options objects, so it holds for
   /// options that were not built for the contract: the default ones, the web ones, ones that indent, ones that
   /// ignore letter case, and the contract's own two. None of them reads a request of another version with a
   /// malformed operation and a member the envelope does not have, a reply without a result, an error with a raw
   /// message or a payload. None of them writes an envelope either, because options of a caller's choosing could
   /// write it in another form than the contract's one line; the refusal names the function that does write it.
   /// </summary>
   [Fact]
   public void Envelope_IsNeitherReadNorWrittenByTheSerializer_UnderOptionsThatBringNoConverterForIt()
   {
      JsonSerializerOptions[] all = [JsonSerializerOptions.Default, JsonSerializerOptions.Web, new JsonSerializerOptions { WriteIndented = true },
         new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ManagementJson.Options, ManagementJson.StrictOptions];
      var request = ManagementRequest.For(ManagementOperations.PrintersShare, Server, new PrintersShareArgs("printer-a1", Shared: true));
      var reply = ManagementReply.Failure(ManagementErrorCode.Busy, "Busy.");

      Assert.All(all, options =>
      {
         Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<ManagementRequest>("{\"V\":7,\"Op\":\"Not An Operation\",\"Extra\":1}", options));
         Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<ManagementRequest>("{\"v\":7,\"op\":\"Not An Operation\",\"extra\":1,\"server\":\"x\"}", options));
         Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<ManagementReply>("{\"v\":9,\"ok\":true}", options));
         Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<ManagementError>("{\"code\":\"busy\",\"message\":\"raw\\u202E\\ntext\"}", options));
         Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<ManagementPayload>("{}", options));
         NotSupportedException written = Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(request, options));
         Assert.Contains("ManagementJson.Write", written.Message, StringComparison.Ordinal);
         Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(reply, options));
         Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(reply.Error, options));
         Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(request.Args, options));
         Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new { envelope = request }, options));
      });
      Assert.Equal("{\"v\":2,\"op\":\"printers.share\",\"server\":\"" + Server + "\",\"args\":{\"printerId\":\"printer-a1\",\"shared\":true}}", ManagementJson.Write(request));
      Assert.Equal("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"busy\",\"message\":\"Busy.\"}}", ManagementJson.Write(reply));
      Assert.Throws<ArgumentNullException>(() => ManagementJson.Write((ManagementRequest)null!));
      Assert.Throws<ArgumentNullException>(() => ManagementJson.Write((ManagementReply)null!));
   }



   /// <summary>
   /// Where the refusal ends, held so that no document says more: options that carry a converter written for an
   /// envelope type are asked before the type is, so with them the serializer writes and reads that type as the
   /// converter says. Such a converter has to be written on purpose, as the one in this test is; the default options
   /// and every options object the product builds have none, and under those the refusal holds (the test above).
   /// </summary>
   [Fact]
   public void AConverterPlacedInTheOptionsForAnEnvelopeType_IsAskedBeforeTheRefusalOnTheType()
   {
      JsonSerializerOptions withConverter = new() { Converters = { new ReplyAsFixedText() } };
      var reply = ManagementReply.Failure(ManagementErrorCode.Busy, "Busy.");

      Assert.Equal("\"written by a converter in the options\"", JsonSerializer.Serialize(reply, withConverter));
      Assert.Equal(ManagementErrorCode.InternalError, JsonSerializer.Deserialize<ManagementReply>("{\"v\":9}", withConverter)!.Error!.Code);
      Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(reply.Error, withConverter));
      Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(reply, JsonSerializerOptions.Default));
   }



   /// <summary>
   /// An envelope is written as one line with the escaping the serializer's default gives, the same its arguments were
   /// written with: a character outside ASCII, a quote, a line break and the characters a browser could misread are
   /// all escapes, in the envelope's own members and inside the arguments, and the text reads back as what was written.
   /// </summary>
   [Fact]
   public void Envelope_IsWrittenAsOneEscapedLine()
   {
      var request = ManagementRequest.For(ManagementOperations.ServersRename, Server, new ServersRenameArgs("Caf\u00E9 \"east\" <1>"));
      var reply = ManagementReply.Failure(ManagementErrorCode.ServerRefused, "Refus\u00E9: \"non\" <b>&</b>");

      string writtenRequest = ManagementJson.Write(request);
      string writtenReply = ManagementJson.Write(reply);

      Assert.Equal("{\"v\":2,\"op\":\"servers.rename\",\"server\":\"" + Server + "\",\"args\":{\"name\":\"Caf\\u00E9 \\u0022east\\u0022 \\u003C1\\u003E\"}}", writtenRequest);
      Assert.Equal("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"server_refused\",\"message\":\"Refus\\u00E9: \\u0022non\\u0022 \\u003Cb\\u003E\\u0026\\u003C/b\\u003E\"}}", writtenReply);
      Assert.All(new[] { writtenRequest, writtenReply }, text => Assert.True(text.All(char.IsAscii) && !text.Contains('\n', StringComparison.Ordinal)));
      Assert.True(ManagementJson.TryReadArguments(ManagementJson.ParseRequest(writtenRequest).Request!, out ServersRenameArgs? renamed));
      Assert.Equal("Caf\u00E9 \"east\" <1>", renamed.Name);
      Assert.Equal("Refus\u00E9: \"non\" <b>&</b>", ManagementJson.ParseReply(writtenReply).Reply!.Error!.Message);
   }



   /// <summary>A payload is a JSON object and nothing else. It is written as that object inside the envelope that carries it, as a request's arguments and as an error's details, says whether it is empty, and like the envelope it is never read or written by the serializer.</summary>
   [Fact]
   public void Payload_IsAJsonObject_AndNothingElse()
   {
      ManagementPayload payload = Payload("{\"printerId\":\"printer-a1\",\"shared\":true}");

      Assert.Equal("{\"v\":2,\"op\":\"printers.share\",\"args\":{\"printerId\":\"printer-a1\",\"shared\":true}}", ManagementJson.Write(ManagementRequest.For(ManagementOperations.PrintersShare, null, payload)));
      Assert.Equal("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"code_added_later\",\"message\":\"Synthetic message.\",\"details\":{\"printerId\":\"printer-a1\",\"shared\":true}}}",
         ManagementJson.Write(ManagementReply.Failure(ErrorRead("code_added_later", "{\"printerId\":\"printer-a1\",\"shared\":true}"))));
      Assert.False(payload.IsEmpty());
      Assert.True(Payload("{}").IsEmpty());
      NotSupportedException refusal = Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<ManagementPayload>("{}", ManagementJson.Options));
      Assert.Contains("ManagementJson.ParseRequest", refusal.Message, StringComparison.Ordinal);
      Assert.Contains("ManagementJson.ParseReply", refusal.Message, StringComparison.Ordinal);
      Assert.Throws<ArgumentException>(() => ManagementPayload.FromElement(JsonDocument.Parse("[1]").RootElement));
      Assert.Throws<ArgumentException>(() => ManagementPayload.FromElement(JsonDocument.Parse("\"text\"").RootElement));
   }



   /// <summary>
   /// A member that may be null may be absent, unless its record marks it as required, by the attribute or by the
   /// language's keyword: then it must be written, null or not. Without this a record could not say "this must be
   /// present" about a member that may be null.
   /// </summary>
   [Fact]
   public void Result_ThatLeavesOutAMemberMarkedRequired_IsNotRead()
   {
      Assert.True(ManagementJson.TryReadResult(Result("{\"must\":null,\"alsoMust\":null}"), out MarkedRecord? nulls));
      Assert.Equal(new MarkedRecord(null, null) { AlsoMust = null }, nulls);
      Assert.True(ManagementJson.TryReadResult(Result("{\"must\":\"a\",\"may\":\"b\",\"alsoMust\":\"c\"}"), out MarkedRecord? full));
      Assert.Equal(new MarkedRecord("a", "b") { AlsoMust = "c" }, full);
      Assert.False(ManagementJson.TryReadResult(Result("{\"alsoMust\":null}"), out MarkedRecord? _));
      Assert.False(ManagementJson.TryReadResult(Result("{\"must\":null}"), out MarkedRecord? _));
      Assert.False(ManagementJson.TryReadResult(Result("{}"), out MarkedRecord? _));
   }



   /// <summary>
   /// Every error message is made printable and cut at 512 UTF-16 units by both factories, whatever the code, because a
   /// message may repeat what a server said. Control characters, the bidirectional overrides, embeddings and isolates,
   /// the zero-width characters, the line and paragraph separators and a private-use character each become a space;
   /// visible text in any script is kept, and so are the no-break and the ideographic space, which are visible gaps;
   /// a surrogate without its partner is shown as the replacement character. The characters are written as escapes
   /// here so this file holds none of them.
   /// </summary>
   [Fact]
   public void FailureMessage_IsMadePrintable_ByBothFactories()
   {
      const string Hostile = "refused\u0007 \u001B[31mred\nnext\rline\u0000\u202Egnp.exe\u202C \u202Aa\u202Bb \u2066c\u2067d\u2068e\u2069\u2028sep\u2029 zero\u200Bwidth\u200E\u200F \uFEFFmark \uE000private caf\u00E9 \u4E2D\u6587";
      char[] replaced = ['\u0007', '\u001B', '\n', '\r', '\u0000', '\u202E', '\u202C', '\u202A', '\u202B', '\u2066', '\u2067', '\u2068', '\u2069', '\u2028', '\u2029', '\u200B', '\u200E', '\u200F', '\uFEFF', '\uE000'];
      string expected = replaced.Aggregate(Hostile, (text, character) => text.Replace(character, ' '));

      var plain = ManagementReply.Failure(ManagementErrorCode.ServerRefused, Hostile);
      var detailed = ManagementReply.Failure(ManagementErrorCode.CancelRefused, Hostile, new CancelRefusedDetails(CancelRefusedDetails.DocumentGone));
      string written = ManagementJson.Write(plain);

      Assert.Equal(expected, plain.Error!.Message);
      Assert.Equal(expected, detailed.Error!.Message);
      Assert.All(replaced, character => Assert.DoesNotContain(character, plain.Error.Message));
      Assert.EndsWith("private caf\u00E9 \u4E2D\u6587", plain.Error.Message, StringComparison.Ordinal);
      Assert.Equal("a b", ManagementReply.Failure(ManagementErrorCode.ServerRefused, "a\u202Eb").Error!.Message);
      Assert.Equal("a\uFFFDb", ManagementReply.Failure(ManagementErrorCode.ServerRefused, "a\uD83Db").Error!.Message);
      Assert.Equal("a\u00A0b\u3000c", ManagementReply.Failure(ManagementErrorCode.ServerRefused, "a\u00A0b\u3000c").Error!.Message);
      Assert.DoesNotContain("\\u202E", written, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("\\u001B", written, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("\\u2028", written, StringComparison.OrdinalIgnoreCase);
      Assert.Equal(expected, ManagementJson.ParseReply(written).Reply!.Error!.Message);
      Assert.Equal("Management reply v2 failed: server_refused", plain.ToString());
      Assert.Equal("server_refused: " + expected, plain.Error.ToString());
   }



   /// <summary>
   /// An error message longer than 512 UTF-16 units is cut there, exactly: nothing is added to mark the cut, so no
   /// message is longer than the bound. The cut never falls inside a surrogate pair: a pair that would straddle the
   /// limit is left out whole, which leaves 511 units, and one that ends exactly on it is kept whole. A client that
   /// reads the reply applies the same rule again and holds the same text.
   /// </summary>
   [Fact]
   public void FailureMessage_IsCutAtTheLimitExactly_NeverInsideASurrogatePair()
   {
      const string Pair = "\uD83D\uDE00";
      string tooLong = ManagementReply.Failure(ManagementErrorCode.ServerRefused, new string('x', 600)).Error!.Message;
      string straddling = ManagementReply.Failure(ManagementErrorCode.ServerRefused, new string('x', 511) + Pair + "tail").Error!.Message;
      string fitting = ManagementReply.Failure(ManagementErrorCode.ServerRefused, new string('x', 510) + Pair + "tail").Error!.Message;
      string exact = ManagementReply.Failure(ManagementErrorCode.ServerRefused, new string('x', 512)).Error!.Message;
      string oneOver = ManagementReply.Failure(ManagementErrorCode.ServerRefused, new string('x', 513)).Error!.Message;

      Assert.Equal(new string('x', 512), tooLong);
      Assert.Equal(new string('x', 511), straddling);
      Assert.Equal(new string('x', 510) + Pair, fitting);
      Assert.Equal(new string('x', 512), exact);
      Assert.Equal(new string('x', 512), oneOver);
      Assert.Equal(512, ManagementProtocol.MaximumMessageCharacters);
      Assert.All(new[] { tooLong, straddling, fitting, exact, oneOver }, message =>
      {
         Assert.True(message.Length <= ManagementProtocol.MaximumMessageCharacters);
         Assert.False(char.IsHighSurrogate(message[^1]));
         Assert.Equal(message, ManagementJson.ParseReply(ManagementJson.Write(ManagementReply.Failure(ManagementErrorCode.ServerRefused, message))).Reply!.Error!.Message);
      });
      Assert.True(ManagementJson.ParseReply(ManagementJson.Write(ManagementReply.Failure(ManagementErrorCode.ServerRefused, new string('x', 100000)))).Succeeded());
   }



   /// <summary>
   /// A failure the service builds must say something. A message that is missing, empty, blank, or blank once it is
   /// printable is refused by every way of building an error, and no words such as "(missing)" are sent in its place:
   /// a server that refused "with no reason" leaves the handler to say so in its own words. A client still reads a
   /// reply whose message is empty, so that it learns the code.
   /// </summary>
   [Theory]
   [InlineData("")]
   [InlineData("   ")]
   [InlineData("\u00A0\u3000")]
   [InlineData("\n\t\r")]
   [InlineData("\u202E\u200B\uFEFF")]
   public void FailureMessage_WithNothingToShow_IsRefused(string message)
   {
      CancelRefusedDetails details = new(CancelRefusedDetails.DocumentGone);

      Assert.Throws<ArgumentException>(() => ManagementReply.Failure(ManagementErrorCode.ServerRefused, message));
      Assert.Throws<ArgumentException>(() => ManagementReply.Failure(ManagementErrorCode.CancelRefused, message, details));
      Assert.Throws<ArgumentException>(() => new ManagementError(ManagementErrorCode.InternalError, message));
      Assert.Throws<ArgumentException>(() => ManagementError.WithDetails(ManagementErrorCode.CancelRefused, message, details));
      Assert.Throws<ArgumentNullException>(() => ManagementReply.Failure(ManagementErrorCode.ServerRefused, null!));
      Assert.Throws<ArgumentNullException>(() => ManagementReply.Failure(ManagementErrorCode.CancelRefused, null!, details));
      ManagementReplyParse read = ManagementJson.ParseReply("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"server_refused\",\"message\":" + JsonSerializer.Serialize(message) + "}}");
      Assert.True(read.Succeeded());
      Assert.Equal(ManagementErrorCode.ServerRefused, read.Reply.Error!.Code);
      Assert.True(string.IsNullOrWhiteSpace(read.Reply.Error.Message));
      Assert.DoesNotContain("missing", read.Reply.Error.Message, StringComparison.Ordinal);
   }



   /// <summary>
   /// The form and the bound of an error's message hold however the error is made, not only through the reply
   /// factories: built by its constructor, handed to the factory that takes an error, built by the factory that
   /// takes a code and a message, and read from the text of a service that did not bound it. Each
   /// carries the same printable, cut message, and none of them writes the raw text. An error has one constructor and
   /// no member that can be set afterwards, so there is no second way to give it a message.
   /// </summary>
   [Fact]
   public void ErrorMessage_IsPrintableAndBounded_HoweverTheErrorIsMade()
   {
      string hostile = "refused\u202Egnp.exe\nnext\u2028line " + new string('x', 2000);
      ManagementError built = new(ManagementErrorCode.ServerRefused, hostile);
      var byFactory = ManagementReply.Failure(built);
      var byCode = ManagementReply.Failure(ManagementErrorCode.ServerRefused, hostile);
      ManagementError read = ManagementJson.ParseReply("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"server_refused\",\"message\":" + JsonSerializer.Serialize(hostile) + "}}").Reply!.Error!;

      string expected = ManagementReply.Failure(ManagementErrorCode.ServerRefused, hostile).Error!.Message;

      Assert.StartsWith("refused gnp.exe next line xxx", expected, StringComparison.Ordinal);
      Assert.Equal(ManagementProtocol.MaximumMessageCharacters, expected.Length);
      Assert.All(new[] { built, byFactory.Error!, byCode.Error!, read }, error => Assert.Equal(expected, error.Message));
      Assert.All(new[] { byFactory, byCode }, reply =>
      {
         string written = ManagementJson.Write(reply);
         Assert.DoesNotContain("\\u202E", written, StringComparison.OrdinalIgnoreCase);
         Assert.DoesNotContain("\\n", written, StringComparison.Ordinal);
         Assert.DoesNotContain("\\u2028", written, StringComparison.OrdinalIgnoreCase);
         Assert.InRange(written.Length, expected.Length, expected.Length + 80);
      });
      Assert.Single(typeof(ManagementError).GetConstructors());
      Assert.All(typeof(ManagementError).GetProperties(), member => Assert.Null(member.SetMethod));
   }



   /// <summary>The refusal the routing check returns is sent by the factory that takes an error, as it is, so the dispatcher's first reply does not have to be built member by member; a missing error or a missing code is refused.</summary>
   [Fact]
   public void RoutingRefusal_IsSentByTheFactoryThatTakesAnError()
   {
      ManagementError refusal = ManagementOperations.Check(ManagementRequest.For("servers.explode"), out _)!;

      var reply = ManagementReply.Failure(refusal);

      Assert.Equal((ManagementProtocol.Version, false), (reply.V, reply.Ok));
      Assert.Same(refusal, reply.Error);
      Assert.Null(reply.Result);
      Assert.Equal("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"unknown_operation\",\"message\":\"There is no operation with this name.\"}}", ManagementJson.Write(reply));
      Assert.Throws<ArgumentNullException>(() => ManagementReply.Failure(null!));
      Assert.Throws<ArgumentNullException>(() => new ManagementError(null!, "Synthetic message."));
   }



   /// <summary>
   /// A failure the service builds carries a code from the table and exactly the details the table names for it. Text
   /// that is not a code is refused, with or without details, and the refusal does not repeat it. A code that carries no
   /// details cannot be given any; a code that carries details cannot be sent without them, with another code's record,
   /// or with an object that merely has the right members, which would skip what the record's own construction
   /// enforces (the unbounded principal here would otherwise be written whole). Every code in the table is accepted
   /// with its own details, or with none when it carries none.
   /// </summary>
   [Fact]
   public void Failure_TiesTheDetailsToTheCode_AndRefusesACodeThatIsNotInTheTable()
   {
      CancelRefusedDetails cancel = new(CancelRefusedDetails.DocumentGone);
      var lookalike = new { approvedPrincipalId = new string('p', 2028), approvedTenantId = "tenant\u202E2" };

      ArgumentException notACode = Assert.Throws<ArgumentException>(() => ManagementReply.Failure("Synthetic Text\n", "Synthetic message."));
      Assert.DoesNotContain("Synthetic Text", notACode.Message, StringComparison.Ordinal);
      Assert.Throws<ArgumentException>(() => new ManagementError("code_added_later", "Synthetic message."));
      Assert.Throws<ArgumentException>(() => ManagementReply.Failure("code_added_later", "Synthetic message.", cancel));
      Assert.Throws<ArgumentException>(() => ManagementReply.Failure("", "Synthetic message."));
      Assert.Throws<ArgumentNullException>(() => ManagementReply.Failure(null!, "Synthetic message.", cancel));
      Assert.Throws<ArgumentException>(() => ManagementReply.Failure(ManagementErrorCode.Busy, "Synthetic message.", cancel));
      Assert.Throws<ArgumentException>(() => ManagementReply.Failure(ManagementErrorCode.IdentityChanged, "Synthetic message.", cancel));
      Assert.Throws<ArgumentException>(() => ManagementReply.Failure(ManagementErrorCode.IdentityChanged, "Synthetic message.", lookalike));
      Assert.Throws<ArgumentException>(() => ManagementError.WithDetails<object>(ManagementErrorCode.CancelRefused, "Synthetic message.", cancel));
      Assert.Throws<ArgumentException>(() => ManagementReply.Failure(ManagementErrorCode.CancelRefused, "Synthetic message."));
      Assert.Throws<ArgumentException>(() => new ManagementError(ManagementErrorCode.IdentityChanged, "Synthetic message."));
      Assert.Throws<ArgumentNullException>(() => ManagementReply.Failure<CancelRefusedDetails>(ManagementErrorCode.CancelRefused, "Synthetic message.", null!));
      Assert.All(ManagementErrorCode.All, code =>
      {
         ManagementError error = ManagementSamples.Failure(code).Error!;
         Assert.Equal((code, ManagementErrorCode.DetailsType(code) is not null), (error.Code, error.Details is not null));
      });
      Assert.Equal("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"cancel_refused\",\"message\":\"Synthetic message.\",\"details\":{\"reason\":\"documentGone\"}}}",
         ManagementJson.Write(ManagementReply.Failure(ManagementError.WithDetails(ManagementErrorCode.CancelRefused, "Synthetic message.", cancel))));
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
   private static ManagementRequest Request(string arguments) => ManagementRequest.For(ManagementOperations.Snapshot, null, Payload(arguments));



   /// <summary>A successful reply with the given result object.</summary>
   private static ManagementReply Result(string result) => ManagementReply.Success(Payload(result));



   /// <summary>An error as a client reads it from the wire, with the given code and details object. The service cannot build an error whose details do not belong to its code, so a test that needs one reads it, as a client would from a service that sent it.</summary>
   private static ManagementError ErrorRead(string code, string details)
      => ManagementJson.ParseReply("{\"v\":2,\"ok\":false,\"error\":{\"code\":\"" + code + "\",\"message\":\"Synthetic message.\",\"details\":" + details + "}}").Reply!.Error!;



   /// <summary>A JSON object as the payload an envelope carries. It is parsed the lenient way on purpose, so a test can hand the typed readers something the envelope parser would have refused.</summary>
   private static ManagementPayload Payload(string json) => ManagementPayload.FromElement(JsonDocument.Parse(json).RootElement);



   /// <summary>A converter for the reply type of the kind only a deliberate caller writes: it writes every reply as one fixed text and reads any text as one fixed failure. It exists to show that options carrying it are asked before the refusal declared on the type.</summary>
   private sealed class ReplyAsFixedText : JsonConverter<ManagementReply>
   {
      /// <summary>Skips whatever is there and returns a fixed failure.</summary>
      public override ManagementReply Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
      {
         reader.Skip();
         return ManagementReply.Failure(ManagementErrorCode.InternalError, "Read by a converter in the options.");
      }



      /// <summary>Writes a fixed text, whatever the reply holds.</summary>
      public override void Write(Utf8JsonWriter writer, ManagementReply value, JsonSerializerOptions options) => writer.WriteStringValue("written by a converter in the options");
   }



   /// <summary>A record with a dictionary of lists, which no record of the contract has, for the test of the walk that looks for a null inside a list.</summary>
   private sealed record DictionaryRecord(IReadOnlyDictionary<string, IReadOnlyList<string>> Lists);



   /// <summary>A record with a value type that may be null and holds a list, which no record of the contract has, for the same test.</summary>
   private sealed record NullableValueRecord(Holder? Maybe);



   /// <summary>The value type inside <see cref="NullableValueRecord"/>.</summary>
   private readonly record struct Holder(IReadOnlyList<string> Items);



   /// <summary>A record whose members may all be null: one marked required by the attribute, one by the keyword, and one not marked, which is the only one that may be absent.</summary>
   private sealed record MarkedRecord([property: JsonRequired] string? Must, string? May)
   {
      /// <summary>May be null, but must be written.</summary>
      public required string? AlsoMust { get; init; }
   }
}
