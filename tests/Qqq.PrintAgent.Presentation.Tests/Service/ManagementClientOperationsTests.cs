/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Reflection;
using System.Text.Json;
using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Service;

/// <summary>
/// The named operations of the version 2 client, held to the contract's own golden files: each operation the
/// companion sends has one method, which sends exactly the golden request and reads the golden reply into the
/// operation's result record; each operation it does not send is listed with its reason; and no method lets a
/// request, a reply or a payload through in either direction.
/// </summary>
public sealed class ManagementClientOperationsTests
{
   /// <summary>The server the requests of these tests name.</summary>
   private const string Server = "3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f";



   /// <summary>The name of the argument that is the check the operator was shown.</summary>
   private const string ShownCheck = "checkTheOperatorSaw";



   /// <summary>
   /// Walks the contract's table. An operation the companion sends has exactly one call, and that call, given the
   /// golden values, sends one request that is the operation's golden file byte for byte once the file's indentation
   /// is removed, which is the one line the wire carries. An operation the companion does not send has no call and a
   /// reason. The two lists cover the table exactly, so an operation added to the contract fails here.
   /// </summary>
   [Fact]
   public async Task EveryOperation_IsSentByOneMethodWithItsGoldenRequest_OrIsListedAsNotTheCompanions()
   {
      foreach(ManagementOperationInfo operation in ManagementOperations.All)
      {
         CompanionCall[] calls = [.. CompanionCalls.All.Where(call => string.Equals(call.Operation, operation.Name, StringComparison.Ordinal))];
         if(CompanionCalls.NotTheCompanions.TryGetValue(operation.Name, out string? reason))
         {
            Assert.Empty(calls);
            Assert.False(string.IsNullOrWhiteSpace(reason), operation.Name);
            continue;
         }
         CompanionCall call = Assert.Single(calls);
         FakePipe pipe = new FakePipe().Answer(Replies.Golden(operation.Name + ".reply.json"));

         CallResult result = await call.Send(new ManagementClient(pipe.SendAsync, new RecordingLog()), SampleValues.Golden, CancellationToken.None);

         Assert.True(result.Outcome == ServiceOutcome.Success, operation.Name);
         Assert.Single(pipe.Sent);
         Assert.Equal(Replies.GoldenText(operation.Name + ".request.json"), pipe.Wire(0));
      }
      Assert.Equal([ManagementOperations.ServersList, ManagementOperations.ComputerGetProfile, ManagementOperations.UpdatePrepare, ManagementOperations.UpdateResume], CompanionCalls.NotTheCompanions.Keys, StringComparer.Ordinal);
      Assert.Equal(ManagementOperations.All.Count, CompanionCalls.All.Count + CompanionCalls.NotTheCompanions.Count);
   }



   /// <summary>
   /// The client's public methods are exactly the calls, one for each operation and none besides: no method is
   /// overloaded, each returns the result record the contract's table names for its operation, and each ends with a
   /// cancellation token that may be left out.
   /// </summary>
   [Fact]
   public void EveryPublicMethod_SendsOneOperation_AndReturnsItsResultRecord()
   {
      MethodInfo[] methods = typeof(ManagementClient).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

      Assert.Equal(CompanionCalls.All.Select(call => call.Method).Order(StringComparer.Ordinal), methods.Select(method => method.Name).Order(StringComparer.Ordinal));
      Assert.Equal(26, methods.Length);
      foreach(CompanionCall call in CompanionCalls.All)
      {
         Assert.True(ManagementOperations.TryFind(call.Operation, out ManagementOperationInfo? operation));
         MethodInfo method = Assert.Single(methods, candidate => string.Equals(candidate.Name, call.Method, StringComparison.Ordinal));
         ParameterInfo last = method.GetParameters()[^1];

         Assert.Equal(typeof(Task<>).MakeGenericType(typeof(ServiceResult<>).MakeGenericType(operation.ResultType)), method.ReturnType);
         Assert.Equal((typeof(CancellationToken), true), (last.ParameterType, last.HasDefaultValue));
      }
   }



   /// <summary>
   /// No method takes or returns a request, a reply, a parse result, a payload, an error or a version 1 pipe request,
   /// directly or inside another type, and a result exposes none of them either. The one constructor takes the send
   /// function and the log, and no server's name.
   /// </summary>
   [Fact]
   public void NoMethodTakesOrReturnsAnEnvelope_AndNoConstructorTakesAServerName()
   {
      Type[] envelope = [typeof(ManagementRequest), typeof(ManagementReply), typeof(ManagementReplyParse), typeof(ManagementRequestParse), typeof(ManagementPayload), typeof(ManagementError), typeof(PipeRequest)];
      MethodInfo[] methods = typeof(ManagementClient).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

      IEnumerable<Type> inMethods = methods.SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType)).SelectMany(WithItsParts);
      IEnumerable<Type> inResults = typeof(ServiceResult<>).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.PropertyType).SelectMany(WithItsParts);
      ConstructorInfo constructor = Assert.Single(typeof(ManagementClient).GetConstructors());

      Assert.DoesNotContain(inMethods, type => envelope.Contains(type));
      Assert.DoesNotContain(inResults, type => envelope.Contains(type));
      Assert.Contains(typeof(ManagementRequest), WithItsParts(typeof(Func<ManagementRequest, Task<ManagementReplyParse>>)));
      Assert.Equal([typeof(Func<ManagementRequest, Task<ManagementReplyParse>>), typeof(IPresentationLog)], constructor.GetParameters().Select(parameter => parameter.ParameterType));
   }



   /// <summary>Every operation's golden reply is a success whose payload is the result record the contract's table names, holding everything the file holds: written again it is the same document. No reply text is kept and nothing is logged.</summary>
   [Theory]
   [MemberData(nameof(CompanionCalls.Sent), MemberType = typeof(CompanionCalls))]
   public async Task Success_IsReadIntoTheOperationsResultRecord(string operation)
   {
      Assert.True(ManagementOperations.TryFind(operation, out ManagementOperationInfo? known));
      RecordingLog log = new();
      FakePipe pipe = new FakePipe().Answer(Replies.Golden(operation + ".reply.json"));

      CallResult result = await CompanionCalls.For(operation).Send(new ManagementClient(pipe.SendAsync, log), SampleValues.Golden, CancellationToken.None);

      Assert.Equal((ServiceOutcome.Success, null, ServiceFailure.None, false), (result.Outcome, result.Code, result.Failure, result.Unconfirmed));
      Assert.IsType(known.ResultType, result.Payload);
      Assert.Equal(Replies.GoldenText(operation + ".reply.json"),
         ManagementJson.Write(ManagementReply.Success(ManagementPayload.FromElement(JsonSerializer.SerializeToElement(result.Payload, known.ResultType, ManagementJson.Options)))));
      Assert.Null(result.Reply);
      Assert.Empty(log.Entries);
   }



   /// <summary>
   /// A reply that holds another job's details than the one asked for is unreadable, so details can never be shown
   /// under the wrong row; the same reply for the job that was asked for is read. A job's identifier is compared
   /// exactly: one that differs only in the case of its letters is another job.
   /// </summary>
   [Fact]
   public async Task JobDetailsForAnotherJob_AreUnreadable()
   {
      RecordingLog log = new();
      FakePipe pipe = new FakePipe().Answer(Replies.Golden("jobs.details.reply.json")).Answer(Replies.Golden("jobs.details.reply.json")).Answer(Replies.Golden("jobs.details.reply.json"));
      ManagementClient client = new(pipe.SendAsync, log);

      ServiceResult<JobDetailsSnapshot> another = await client.JobDetailsAsync(Server, "job-205");
      ServiceResult<JobDetailsSnapshot> inAnotherCase = await client.JobDetailsAsync(Server, "JOB-204");
      ServiceResult<JobDetailsSnapshot> asked = await client.JobDetailsAsync(Server, "job-204");

      Assert.Equal((ServiceOutcome.Unavailable, ServiceFailure.UnreadableReply, true), (another.Outcome, another.Failure, another.Unconfirmed));
      Assert.Equal((ServiceOutcome.Unavailable, ServiceFailure.UnreadableReply, true), (inAnotherCase.Outcome, inAnotherCase.Failure, inAnotherCase.Unconfirmed));
      Assert.Null(another.Payload);
      Assert.Null(inAnotherCase.Payload);
      Assert.Equal("The service's reply could not be read.", another.Message);
      Assert.True(asked.Succeeded);
      Assert.Equal("job-204", asked.Payload.Job.JobId);
      Assert.Equal(
      [
         $"WARN Management request jobs.details on {Server} succeeded, but its result could not be read as JobDetailsSnapshot.",
         $"DEBUG Management request jobs.details on {Server} succeeded, but its result could not be read as JobDetailsSnapshot.",
         $"INFO Management request jobs.details on {Server} is answering again.",
      ], log.Entries);
   }



   /// <summary>
   /// A reply that names its subject is the answer only when the subject is the one that was asked about; any other
   /// is unreadable, and unconfirmed, because the service did something and what it did is not known here. A rename
   /// answered with another server's summary would put the name on the wrong server. An add answered with another
   /// address is not the server that was added; the address is compared as an origin, so one place written two ways
   /// is one place. A reorder answered with other servers than those given is not the list that was ordered; the
   /// order itself is the service's to say. A server's identifier is compared exactly, as a job's is: the contract
   /// gives an identifier one spelling, so the same letters in upper case are not that server.
   /// </summary>
   [Fact]
   public async Task ReplyForAnotherSubject_IsUnreadable()
   {
      RecordingLog log = new();
      ManagementReplyParse renamed = Replies.Golden("servers.rename.reply.json");
      ManagementReplyParse added = Replies.Golden("servers.add.reply.json");
      ManagementReplyParse reordered = Replies.Golden("servers.reorder.reply.json");
      ServerSummary inUpperCase = new(SampleValues.Golden.ServerId.ToUpperInvariant(), "Warehouse", "https://qqq.example.test", 0, "GREEN", "Connected.");
      FakePipe pipe = new FakePipe().Answer(renamed).Answer(added).Answer(added).Answer(reordered).Answer(reordered).Answer(reordered)
         .Answer(Replies.Success(inUpperCase)).Answer(Replies.Success(new ServerListResult([inUpperCase])))
         .Answer(renamed).Answer(added).Answer(reordered);
      ManagementClient client = new(pipe.SendAsync, log);
      SampleValues given = SampleValues.Golden;

      CallResult[] forAnother =
      [
         CallResult.Of(await client.RenameServerAsync(given.SecondServerId, given.NewName)),
         CallResult.Of(await client.AddServerAsync(new Uri("https://bench.example.test"), given.Name, acceptCertificateException: false, given.Check, given.Credential)),
         CallResult.Of(await client.AddServerAsync(new Uri("https://qqq.example.test:8443"), given.Name, acceptCertificateException: false, given.Check, given.Credential)),
         CallResult.Of(await client.ReorderServersAsync([given.ServerId])),
         CallResult.Of(await client.ReorderServersAsync([given.ServerId, "3f2b8c1e-1111-4f6b-9a7c-0e1d2c3b4a61"])),
         CallResult.Of(await client.ReorderServersAsync([given.ServerId, given.ServerId])),
         CallResult.Of(await client.RenameServerAsync(given.ServerId, given.NewName)),
         CallResult.Of(await client.ReorderServersAsync([given.ServerId])),
      ];
      CallResult[] forTheOneAsked =
      [
         CallResult.Of(await client.RenameServerAsync(given.ServerId, given.NewName)),
         CallResult.Of(await client.AddServerAsync(new Uri("HTTPS://QQQ.Example.Test:443/"), given.Name, acceptCertificateException: false, given.Check, given.Credential)),
         CallResult.Of(await client.ReorderServersAsync([given.ServerId, given.SecondServerId])),
      ];

      Assert.All(forAnother, result => Assert.Equal((ServiceOutcome.Unavailable, ServiceFailure.UnreadableReply, true, "The service's reply could not be read."), (result.Outcome, result.Failure, result.Unconfirmed, result.Message)));
      Assert.All(forAnother, result => Assert.Null(result.Payload));
      Assert.All(forTheOneAsked, result => Assert.Equal(ServiceOutcome.Success, result.Outcome));
      Assert.Equal(11, pipe.Sent.Count);
      Assert.Contains($"WARN Management request servers.rename on {given.SecondServerId} succeeded, but its result could not be read as ServerSummary.", log.Entries, StringComparer.Ordinal);
      Assert.Contains("WARN Management request servers.add on the computer succeeded, but its result could not be read as ServerAddResult.", log.Entries, StringComparer.Ordinal);
      Assert.Contains("WARN Management request servers.reorder on the computer succeeded, but its result could not be read as ServerListResult.", log.Entries, StringComparer.Ordinal);
   }



   /// <summary>
   /// A value the contract refuses when a request is built (a name that breaks the name rule, a check whose provider
   /// is text that names no origin, text that is not a server's full identifier) is not sent. The result is the
   /// refusal the service would give for it, <c>invalid_request</c>, with nothing to check; its message is fixed;
   /// and the log names the operation and nothing that was given.
   /// </summary>
   [Fact]
   public async Task ValueTheContractRefuses_IsNotSent_AndIsAnsweredAsTheServiceWould()
   {
      RecordingLog log = new();
      FakePipe pipe = new();
      ManagementClient client = new(pipe.SendAsync, log);
      SampleValues given = SampleValues.Golden;

      CallResult[] results =
      [
         CallResult.Of(await client.AddServerAsync(given.Address, "[CANARY]", acceptCertificateException: false, given.Check, given.Credential)),
         CallResult.Of(await client.AddServerAsync(given.Address, given.Name, acceptCertificateException: false, CheckNaming("CANARY, no origin"), given.Credential)),
         CallResult.Of(await client.RenameServerAsync(Server, "  ")),
         CallResult.Of(await client.SetTrustAsync(Server, acceptCertificateException: true, CheckNaming("CANARY, no origin"))),
         CallResult.Of(await client.ReconnectAsync("CANARY-not-an-identifier")),
         CallResult.Of(await client.ReconnectAsync(Server.ToUpperInvariant())),
      ];

      Assert.Empty(pipe.Sent);
      Assert.All(results, AssertRefusedBeforeSending);
      Assert.Equal(ManagementErrorCode.InvalidRequest, ServerName.RefusalCode);
      Assert.Equal(
      [
         .. Enumerable.Repeat("WARN Management request servers.add was not sent: a value given for it is one the contract refuses.", 2),
         "WARN Management request servers.rename was not sent: a value given for it is one the contract refuses.",
         "WARN Management request trust.set was not sent: a value given for it is one the contract refuses.",
         .. Enumerable.Repeat("WARN Management request connection.reconnect was not sent: a value given for it is one the contract refuses.", 2),
      ], log.Entries);
   }



   /// <summary>
   /// Every method that acts on one server refuses, before anything is sent, a server that is not named and text that
   /// is not a server's full identifier: the contract's own check refuses an operation on one server that names none,
   /// and the request cannot be built around text that is no identifier. Without that a request with no server would
   /// leave as a request on the computer. The sixteen methods are those the contract's table gives the scope of one
   /// server.
   /// </summary>
   [Theory]
   [InlineData(null)]
   [InlineData("")]
   [InlineData("CANARY-not-an-identifier")]
   [InlineData("{3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f}")]
   public async Task ServerThatIsNotNamedByItsFullIdentifier_IsNotSent_ByAnyOfTheSixteenMethodsOnOneServer(string? serverId)
   {
      RecordingLog log = new();
      FakePipe pipe = new();
      ManagementClient client = new(pipe.SendAsync, log);
      CompanionCall[] onOneServer = [.. CompanionCalls.All.Where(call => ManagementOperations.TryFind(call.Operation, out ManagementOperationInfo? operation) && operation.Scope == ManagementScope.Server)];

      List<CallResult> results = [];
      foreach(CompanionCall call in onOneServer)
      {
         results.Add(await call.Send(client, SampleValues.Golden with { ServerId = serverId! }, CancellationToken.None));
      }

      Assert.Equal(16, onOneServer.Length);
      Assert.Empty(pipe.Sent);
      Assert.All(results, AssertRefusedBeforeSending);
      Assert.Equal(onOneServer.Select(call => $"WARN Management request {call.Operation} was not sent: a value given for it is one the contract refuses."), log.Entries, StringComparer.Ordinal);
   }



   /// <summary>
   /// A value a request needs and was not given is not sent: the arguments are read back the way the service reads
   /// them, and the contract's reader refuses a record with a hole in it. That is a credential, a code, a job, a
   /// printer, a format, a profile, a query, the log settings, the list of servers, and an item of that list. Sent
   /// all the same, each of these would leave as a request with an argument missing, which the service refuses.
   /// </summary>
   [Fact]
   public async Task ValueThatIsMissing_IsNotSent()
   {
      RecordingLog log = new();
      FakePipe pipe = new();
      ManagementClient client = new(pipe.SendAsync, log);
      SampleValues given = SampleValues.Golden;

      CallResult[] results =
      [
         CallResult.Of(await client.SignInAsync(Server, null!)),
         CallResult.Of(await client.AddServerAsync(given.Address, given.Name, acceptCertificateException: false, given.Check, null!)),
         CallResult.Of(await client.AddServerAsync(given.Address, null!, acceptCertificateException: false, given.Check, given.Credential)),
         CallResult.Of(await client.EnrollAsync(Server, null!)),
         CallResult.Of(await client.ReorderServersAsync(null!)),
         CallResult.Of(await client.ReorderServersAsync([Server, null!])),
         CallResult.Of(await client.SetComputerProfileAsync(null!)),
         CallResult.Of(await client.SetLogSettingsAsync(null!)),
         CallResult.Of(await client.RenameServerAsync(Server, null!)),
         CallResult.Of(await client.SharePrinterAsync(Server, null!, shared: true)),
         CallResult.Of(await client.SetPrinterProfileAsync(Server, given.PrinterId, null!)),
         CallResult.Of(await client.TestPrintAsync(Server, given.PrinterId, null!, testProfile: null)),
         CallResult.Of(await client.QueryJobsAsync(Server, null!)),
         CallResult.Of(await client.JobDetailsAsync(Server, null!)),
         CallResult.Of(await client.RetryJobAsync(Server, null!, confirmDuplicateRisk: true)),
         CallResult.Of(await client.CancelJobAsync(Server, null!)),
         CallResult.Of(await client.AcknowledgeJobAsync(Server, null!)),
      ];

      Assert.Empty(pipe.Sent);
      Assert.All(results, AssertRefusedBeforeSending);
      Assert.Equal(results.Length, log.Entries.Count);
      Assert.All(log.Entries, entry => Assert.EndsWith(" was not sent: a value given for it is one the contract refuses.", entry, StringComparison.Ordinal));
   }



   /// <summary>
   /// An address the contract's one address rule refuses is not sent, by the check and by the add: none at all, a
   /// relative one, one with a path, a query, a fragment or user information, one that is not secure, and one whose
   /// scheme is not a web one. What is sent for an address the rule accepts is the origin it judged, as scheme, host
   /// and port: a default port and a trailing slash are left out, and a host written outside ASCII is the name the
   /// connection is made to.
   /// </summary>
   [Fact]
   public async Task AddressTheContractsRuleRefuses_IsNotSent_AndAnAcceptedOneIsSentAsItsOrigin()
   {
      RecordingLog log = new();
      FakePipe nothingScripted = new();
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient refusing = new(nothingScripted.SendAsync, log);
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());
      SampleValues given = SampleValues.Golden;
      Uri?[] refused =
      [
         null, new Uri("/servers/one", UriKind.Relative), new Uri("https://qqq.example.test/CANARY-path"), new Uri("https://qqq.example.test/?CANARY=1"), new Uri("https://qqq.example.test/#CANARY"),
         new Uri("https://CANARY@qqq.example.test"), new Uri("http://qqq.example.test"), new Uri("ftp://qqq.example.test"),
      ];

      List<CallResult> results = [];
      foreach(Uri? address in refused)
      {
         results.Add(CallResult.Of(await refusing.CheckServerAsync(address!, acceptCertificateException: false)));
         results.Add(CallResult.Of(await refusing.AddServerAsync(address!, given.Name, acceptCertificateException: false, given.Check, given.Credential)));
      }
      Assert.Empty(nothingScripted.Sent);
      _ = client.CheckServerAsync(new Uri("HTTPS://Qqq.Example.Test:443/"), acceptCertificateException: false);
      _ = client.CheckServerAsync(new Uri("https://b\u00fccher.example:8443"), acceptCertificateException: false);
      _ = client.CheckServerAsync(new Uri("http://localhost:18581/"), acceptCertificateException: false);
      await pipe.Arrived(3);

      Assert.All(results, AssertRefusedBeforeSending);
      Assert.Equal(16, log.Entries.Count);
      Assert.DoesNotContain(log.Entries, entry => entry.Contains(SampleValues.Mark, StringComparison.OrdinalIgnoreCase));
      Assert.Equal(
      [
         """{"v":2,"op":"servers.check","args":{"address":"https://qqq.example.test","acceptCertificateException":false}}""",
         """{"v":2,"op":"servers.check","args":{"address":"https://xn--bcher-kva.example:8443","acceptCertificateException":false}}""",
         """{"v":2,"op":"servers.check","args":{"address":"http://localhost:18581","acceptCertificateException":false}}""",
      ], pipe.Sent.Select((_, index) => pipe.Wire(index)), StringComparer.Ordinal);
   }



   /// <summary>
   /// The client refuses what the contract's own check and reader refuse, and adds no rule of its own. Values the
   /// contract's records accept are sent, whatever the service will think of them: text that is not an identifier
   /// inside the list of servers to reorder, an enrollment code, a printer and a format that are empty, and log
   /// limits that are negative. The service answers those; if the contract comes to refuse one of them, it is
   /// refused here from then on, and this test says so.
   /// </summary>
   [Fact]
   public async Task ValueTheContractsReaderAccepts_IsSent_BecauseTheClientAddsNoRuleOfItsOwn()
   {
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());

      _ = client.ReorderServersAsync([Server, "not-an-identifier"]);
      _ = client.EnrollAsync(Server, "");
      _ = client.TestPrintAsync(Server, "", "", testProfile: null);
      _ = client.SetLogSettingsAsync(new LogsSetSettingsArgs(RetentionDays: -1, MaxFileMegabytes: 0, SegmentsPerDay: int.MaxValue, DebugLogging: true));
      await pipe.Arrived(4);

      Assert.Equal(
      [
         $$$"""{"v":2,"op":"servers.reorder","args":{"serverIds":["{{{Server}}}","not-an-identifier"]}}""",
         $$$"""{"v":2,"op":"pairing.enroll","server":"{{{Server}}}","args":{"enrollmentCode":""}}""",
         $$$"""{"v":2,"op":"printers.testPrint","server":"{{{Server}}}","args":{"printerId":"","format":""}}""",
         """{"v":2,"op":"logs.setSettings","args":{"retentionDays":-1,"maxFileMegabytes":0,"segmentsPerDay":2147483647,"debugLogging":true}}""",
      ], pipe.Sent.Select((_, index) => pipe.Wire(index)), StringComparer.Ordinal);
   }



   /// <summary>
   /// The sign-in provider's origin a request carries is the one in the check the caller gives, exactly as the check
   /// reported it and in no other spelling, and a check that named no provider sends a request without the member,
   /// which states that. That holds for adding a server and for switching the exception on. Switching it off accepts
   /// nothing: the request states no provider, with no check given and with one given all the same.
   /// </summary>
   [Fact]
   public async Task ProviderOrigin_IsSentExactlyAsGiven_AndLeftOutWhenTheCheckNamedNone()
   {
      FakePipe pipe = new FakePipe().Answer(Replies.Failure(ManagementErrorCode.ProviderChanged)).Answer(Replies.Failure(ManagementErrorCode.ProviderChanged))
         .Answer(Replies.Failure(ManagementErrorCode.ProviderChanged)).Answer(Replies.Failure(ManagementErrorCode.ProviderChanged)).Answer(Replies.Failure(ManagementErrorCode.ProviderChanged))
         .Answer(Replies.Failure(ManagementErrorCode.ProviderChanged));
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());
      SampleValues given = SampleValues.Golden;
      ServerCheckResult namedOne = CheckNaming("HTTPS://Sign-In.Example.Test:443/");
      ServerCheckResult namedNone = CheckNaming(null);

      ServiceResult<ServerAddResult> added = await client.AddServerAsync(given.Address, given.Name, acceptCertificateException: true, namedOne, given.Credential);
      _ = await client.AddServerAsync(given.Address, given.Name, acceptCertificateException: true, namedNone, given.Credential);
      ServiceResult<TrustResult> trusted = await client.SetTrustAsync(Server, acceptCertificateException: true, namedOne);
      _ = await client.SetTrustAsync(Server, acceptCertificateException: true, namedNone);
      _ = await client.SetTrustAsync(Server, acceptCertificateException: false, checkTheOperatorSaw: null);
      _ = await client.SetTrustAsync(Server, acceptCertificateException: false, namedOne);

      Assert.Equal((ServiceOutcome.Rejected, ManagementErrorCode.ProviderChanged), (added.Outcome, added.Code));
      Assert.Equal((ServiceOutcome.Rejected, ManagementErrorCode.ProviderChanged), (trusted.Outcome, trusted.Code));
      Assert.Contains("\"providerOrigin\":\"HTTPS://Sign-In.Example.Test:443/\"", pipe.Wire(0), StringComparison.Ordinal);
      Assert.DoesNotContain("providerOrigin", pipe.Wire(1), StringComparison.Ordinal);
      Assert.Equal($$$"""{"v":2,"op":"trust.set","server":"{{{Server}}}","args":{"acceptCertificateException":true,"providerOrigin":"HTTPS://Sign-In.Example.Test:443/"}}""", pipe.Wire(2));
      Assert.Equal($$$"""{"v":2,"op":"trust.set","server":"{{{Server}}}","args":{"acceptCertificateException":true}}""", pipe.Wire(3));
      Assert.Equal($$$"""{"v":2,"op":"trust.set","server":"{{{Server}}}","args":{"acceptCertificateException":false}}""", pipe.Wire(4));
      Assert.Equal(pipe.Wire(4), pipe.Wire(5));
   }



   /// <summary>
   /// Adding a server, and switching the exception on, without the check the operator saw is refused as an argument
   /// that is missing, and nothing is sent or logged: without the check there is nothing the operator can have
   /// accepted, and a request sent all the same would state that the check named no provider.
   /// </summary>
   [Fact]
   public async Task AcceptingWithoutTheCheckTheOperatorSaw_IsRefused_AndNothingIsSent()
   {
      RecordingLog log = new();
      FakePipe pipe = new();
      ManagementClient client = new(pipe.SendAsync, log);
      SampleValues given = SampleValues.Golden;

      ArgumentNullException adding = await Assert.ThrowsAsync<ArgumentNullException>(() => client.AddServerAsync(given.Address, given.Name, acceptCertificateException: false, null!, given.Credential));
      ArgumentNullException switchingOn = await Assert.ThrowsAsync<ArgumentNullException>(() => client.SetTrustAsync(Server, acceptCertificateException: true, checkTheOperatorSaw: null));

      Assert.Equal((ShownCheck, ShownCheck), (adding.ParamName, switchingOn.ParamName));
      Assert.Empty(pipe.Sent);
      Assert.Empty(log.Entries);
   }



   /// <summary>
   /// Adding a server with a check that failed is refused as an argument that is wrong, and nothing is sent or
   /// logged: the operator goes on only from a check in which every stage passed, so a failed check is never the one
   /// the operator went on from, and sending it would state a provider (or none) that the operator did not accept.
   /// Both kinds of failure are refused, with or without the exception. Switching a server's exception on is not
   /// held to that: a server whose own certificate is refused is the one an exception is for, so a failed check is
   /// sent there, stating what it reported.
   /// </summary>
   [Fact]
   public async Task AddingWithACheckThatFailed_IsRefused_AndSwitchingTheExceptionOnWithOneIsSent()
   {
      RecordingLog log = new();
      FakePipe pipe = new FakePipe().Answer(Replies.Golden("trust.set.reply.json"));
      ManagementClient client = new(pipe.SendAsync, log);
      SampleValues given = SampleValues.Golden;
      ServerCheckResult certificateRefused = FailedCheck("error.certificate_untrusted.json");
      ServerCheckResult stageFailed = FailedCheck("error.server_incompatible.json");
      Assert.Equal((ManagementErrorCode.CertificateUntrusted, ManagementErrorCode.ServerIncompatible), (certificateRefused.FailureCode(), stageFailed.FailureCode()));

      ArgumentException withTheRefusedCertificate = await Assert.ThrowsAsync<ArgumentException>(() => client.AddServerAsync(given.Address, given.Name, acceptCertificateException: true, certificateRefused, given.Credential));
      ArgumentException withTheFailedStage = await Assert.ThrowsAsync<ArgumentException>(() => client.AddServerAsync(given.Address, given.Name, acceptCertificateException: false, stageFailed, given.Credential));
      Assert.Empty(pipe.Sent);
      ServiceResult<TrustResult> switchedOn = await client.SetTrustAsync(Server, acceptCertificateException: true, certificateRefused);

      Assert.Equal((ShownCheck, ShownCheck), (withTheRefusedCertificate.ParamName, withTheFailedStage.ParamName));
      Assert.StartsWith("The check given is one that failed. A server is added only after a check in which every stage passed.", withTheRefusedCertificate.Message, StringComparison.Ordinal);
      Assert.True(switchedOn.Succeeded);
      Assert.Equal($$$"""{"v":2,"op":"trust.set","server":"{{{Server}}}","args":{"acceptCertificateException":true,"providerOrigin":"https://sign-in.example.test"}}""", pipe.Wire(0));
      Assert.Single(pipe.Sent);
      Assert.Empty(log.Entries);
   }



   /// <summary>
   /// The provider a request states comes from the check the operator saw and from nowhere else: each of the two
   /// methods takes that check's result, with no default, so a call that leaves it out does not compile, and takes
   /// no origin as text, which could hold another read of the provider. Adding a server always needs the check;
   /// switching the exception may be given none, which is for switching it off.
   /// </summary>
   [Theory]
   [InlineData(nameof(ManagementClient.AddServerAsync), NullabilityState.NotNull)]
   [InlineData(nameof(ManagementClient.SetTrustAsync), NullabilityState.Nullable)]
   public void ProviderOrigin_HasNoDefault_SoNoCallerCanLeaveItOut(string method, NullabilityState mayBeLeftNull)
   {
      ParameterInfo[] parameters = typeof(ManagementClient).GetMethod(method)!.GetParameters();
      ParameterInfo shown = Assert.Single(parameters, parameter => string.Equals(parameter.Name, ShownCheck, StringComparison.Ordinal));

      Assert.Equal(typeof(ServerCheckResult), shown.ParameterType);
      Assert.False(shown.HasDefaultValue);
      Assert.False(shown.IsOptional);
      Assert.Equal(mayBeLeftNull, new NullabilityInfoContext().Create(shown).ReadState);
      Assert.DoesNotContain(parameters, parameter => parameter.Name!.Contains("provider", StringComparison.OrdinalIgnoreCase) || parameter.Name.Contains("origin", StringComparison.OrdinalIgnoreCase));
   }



   /// <summary>
   /// The values a caller gives are the values sent, also where the golden request holds the other value: a switch
   /// that is off is sent as off, and an optional value that is not given is left out of the request.
   /// </summary>
   [Fact]
   public async Task ValuesGiven_AreTheValuesSent_AndAnOptionalOneNotGivenIsLeftOut()
   {
      FakePipe pipe = new FakePipe().HoldEverything();
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());
      SampleValues given = SampleValues.Golden;
      string credential = """{"origin":"https://qqq.example.test","sessionToken":"CANARY-SESSION-COOKIE-7f3a"}""";

      _ = client.CheckServerAsync(given.Address, acceptCertificateException: false);
      _ = client.AddServerAsync(given.Address, given.Name, acceptCertificateException: false, CheckNaming(null), given.Credential);
      _ = client.SetLogSettingsAsync(new LogsSetSettingsArgs(RetentionDays: 7, MaxFileMegabytes: 20, SegmentsPerDay: 3, DebugLogging: false));
      _ = client.SignInAsync(Server, given.Credential);
      _ = client.SignOutAsync(Server, postLogoutRedirectUri: null);
      _ = client.SharePrinterAsync(Server, given.PrinterId, shared: false);
      _ = client.TestPrintAsync(Server, given.PrinterId, "ZPL", testProfile: null);
      _ = client.RetryJobAsync(Server, given.JobId, confirmDuplicateRisk: false);
      _ = client.SignInAsync(Server, given.Credential, SignInRepair.NewPairing);
      await pipe.Arrived(9);

      Assert.Equal(
      [
         """{"v":2,"op":"servers.check","args":{"address":"https://qqq.example.test","acceptCertificateException":false}}""",
         """{"v":2,"op":"servers.add","args":{"address":"https://qqq.example.test","name":"Warehouse","acceptCertificateException":false,"sessionCredential":""" + credential + "}}",
         """{"v":2,"op":"logs.setSettings","args":{"retentionDays":7,"maxFileMegabytes":20,"segmentsPerDay":3,"debugLogging":false}}""",
         $$$"""{"v":2,"op":"session.signIn","server":"{{{Server}}}","args":{"sessionCredential":""" + credential + "}}",
         $$$"""{"v":2,"op":"session.signOut","server":"{{{Server}}}","args":{}}""",
         $$$"""{"v":2,"op":"printers.share","server":"{{{Server}}}","args":{"printerId":"printer-a1","shared":false}}""",
         $$$"""{"v":2,"op":"printers.testPrint","server":"{{{Server}}}","args":{"printerId":"printer-a1","format":"ZPL"}}""",
         $$$"""{"v":2,"op":"jobs.retry","server":"{{{Server}}}","args":{"jobId":"job-204","confirmDuplicateRisk":false}}""",
         $$$"""{"v":2,"op":"session.signIn","server":"{{{Server}}}","args":{"sessionCredential":""" + credential + ",\"repair\":\"newPairing\"}}",
      ], pipe.Sent.Select((_, index) => pipe.Wire(index)), StringComparer.Ordinal);
   }



   /// <summary>
   /// A repair is one of the contract's two values and no other: the type that stands for it has exactly those two,
   /// carrying the contract's own constants, and no way to make a third, so a caller cannot pass text of its own
   /// where the service expects one of the two.
   /// </summary>
   [Fact]
   public void Repair_IsOneOfTheContractsTwoValues_AndNoOtherCanBeMade()
   {
      Assert.Equal((SessionSignInArgs.AssociatePairing, SessionSignInArgs.NewPairing), (SignInRepair.AssociatePairing.Value, SignInRepair.NewPairing.Value));
      Assert.True(typeof(SignInRepair).IsSealed);
      Assert.Empty(typeof(SignInRepair).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
      Assert.Equal([nameof(SignInRepair.AssociatePairing), nameof(SignInRepair.NewPairing)],
         typeof(SignInRepair).GetProperties(BindingFlags.Public | BindingFlags.Static).Select(property => property.Name).Order(StringComparer.Ordinal), StringComparer.Ordinal);
      Assert.DoesNotContain(typeof(SignInRepair).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), member => member.MemberType is MemberTypes.Property or MemberTypes.Field);
      Assert.Equal(typeof(SignInRepair), typeof(ManagementClient).GetMethod(nameof(ManagementClient.SignInAsync))!.GetParameters()[2].ParameterType);
   }



   /// <summary>
   /// The methods take the contract's own types where the contract has one, so a caller cannot put values in the
   /// wrong order or give text where a judged value belongs: the log limits are the contract's record, and a
   /// server's address is the origin the contract's address rule returned, not the text an operator typed.
   /// </summary>
   [Fact]
   public void Methods_TakeTheContractsOwnTypes_NotLooseValues()
   {
      static Type[] Parameters(string method) => [.. typeof(ManagementClient).GetMethod(method)!.GetParameters().Select(parameter => parameter.ParameterType)];

      Assert.Equal([typeof(LogsSetSettingsArgs), typeof(CancellationToken)], Parameters(nameof(ManagementClient.SetLogSettingsAsync)));
      Assert.Equal(typeof(Uri), Parameters(nameof(ManagementClient.CheckServerAsync))[0]);
      Assert.Equal(typeof(Uri), Parameters(nameof(ManagementClient.AddServerAsync))[0]);
   }



   /// <summary>The result of a check that failed, as the service reports one: the details of a golden refusal.</summary>
   private static ServerCheckResult FailedCheck(string goldenFile)
   {
      Assert.True(ManagementJson.TryReadDetails(Replies.Golden(goldenFile).Reply!.Error!, out ServerCheckResult? check));
      return check;
   }



   /// <summary>Requires the result of a request that was not sent because the contract refuses a value in it: the refusal the service would give, with a fixed message and nothing to check.</summary>
   private static void AssertRefusedBeforeSending(CallResult result)
      => Assert.Equal(
         (ServiceOutcome.Rejected, ManagementErrorCode.InvalidRequest, "The request was not sent: one of its values is not one the service accepts.", false, ServiceFailure.None),
         (result.Outcome, result.Code, result.Message, result.Unconfirmed, result.Failure));



   /// <summary>The result of a check that named this sign-in provider, or none, as far as the client reads it.</summary>
   private static ServerCheckResult CheckNaming(string? provider) => new([], [ServerCheckResult.Password], provider);



   /// <summary>A type and every type inside it: the arguments of a generic type, and theirs.</summary>
   private static IEnumerable<Type> WithItsParts(Type type) => type.GetGenericArguments().SelectMany(WithItsParts).Prepend(type);
}
