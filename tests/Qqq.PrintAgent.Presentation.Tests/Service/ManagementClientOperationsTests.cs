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



   /// <summary>A reply that holds another job's details than the one asked for is unreadable, so details can never be shown under the wrong row; the same reply for the job that was asked for is read.</summary>
   [Fact]
   public async Task JobDetailsForAnotherJob_AreUnreadable()
   {
      RecordingLog log = new();
      FakePipe pipe = new FakePipe().Answer(Replies.Golden("jobs.details.reply.json")).Answer(Replies.Golden("jobs.details.reply.json"));
      ManagementClient client = new(pipe.SendAsync, log);

      ServiceResult<JobDetailsSnapshot> another = await client.JobDetailsAsync(Server, "job-205");
      ServiceResult<JobDetailsSnapshot> asked = await client.JobDetailsAsync(Server, "job-204");

      Assert.Equal((ServiceOutcome.Unavailable, ServiceFailure.UnreadableReply, true), (another.Outcome, another.Failure, another.Unconfirmed));
      Assert.Null(another.Payload);
      Assert.Equal("The service's reply could not be read.", another.Message);
      Assert.True(asked.Succeeded);
      Assert.Equal("job-204", asked.Payload.Job.JobId);
      Assert.Equal(
      [
         $"WARN Management request jobs.details on {Server} succeeded, but its result could not be read as JobDetailsSnapshot.",
         $"INFO Management request jobs.details on {Server} is answering again.",
      ], log.Entries);
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
      Assert.All(results, result => Assert.Equal(
         (ServiceOutcome.Rejected, ServerName.RefusalCode, "The request was not sent: one of its values is not one the service accepts.", false, ServiceFailure.None),
         (result.Outcome, result.Code, result.Message, result.Unconfirmed, result.Failure)));
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
      _ = client.SetLogSettingsAsync(retentionDays: 7, maxFileMegabytes: 20, segmentsPerDay: 3, debugLogging: false);
      _ = client.SignInAsync(Server, given.Credential);
      _ = client.SignOutAsync(Server, postLogoutRedirectUri: null);
      _ = client.SharePrinterAsync(Server, given.PrinterId, shared: false);
      _ = client.TestPrintAsync(Server, given.PrinterId, "ZPL", testProfile: null);
      _ = client.RetryJobAsync(Server, given.JobId, confirmDuplicateRisk: false);
      await pipe.Arrived(8);

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
      ], pipe.Sent.Select((_, index) => pipe.Wire(index)), StringComparer.Ordinal);
   }



   /// <summary>The result of a check that named this sign-in provider, or none, as far as the client reads it.</summary>
   private static ServerCheckResult CheckNaming(string? provider) => new([], [ServerCheckResult.Password], provider);



   /// <summary>A type and every type inside it: the arguments of a generic type, and theirs.</summary>
   private static IEnumerable<Type> WithItsParts(Type type) => type.GetGenericArguments().SelectMany(WithItsParts).Prepend(type);
}
