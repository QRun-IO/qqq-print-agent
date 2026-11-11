/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Reflection;
using System.Text.Json;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The wire form of version 2 of the management pipe, held by golden files: one request and one reply for every
/// operation, and one failure reply for every error code that carries details. Each file is read through the contract's
/// own types and written again, and must come back as the same document, so a member that is renamed, dropped, reordered
/// or silently not read shows up as a difference. The files are compared after removing only their indentation.
/// </summary>
public sealed class ManagementGoldenTests
{
   /// <summary>Where the golden files are copied to beside the test assembly.</summary>
   private static string GoldenDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "management");



   /// <summary>The name of every operation, for the tests that run once per operation.</summary>
   public static TheoryData<string> OperationNames => [.. ManagementOperations.All.Select(operation => operation.Name)];



   /// <summary>Every error code whose table entry promises details.</summary>
   public static TheoryData<string> CodesWithDetails => [.. ManagementErrorCode.All.Where(code => ManagementErrorCode.DetailsType(code) is not null)];



   /// <summary>The golden directory holds exactly one request and one reply per operation, one failure per error code with details, and the further snapshot examples the tests name: an operation or such a code without its files fails here, and so does a file that belongs to nothing.</summary>
   [Fact]
   public void GoldenFiles_AreExactlyThoseTheContractNeeds()
   {
      IEnumerable<string> expected = ManagementOperations.All.SelectMany(operation => new[] { operation.Name + ".request.json", operation.Name + ".reply.json" })
         .Concat(ManagementErrorCode.All.Where(code => ManagementErrorCode.DetailsType(code) is not null).Select(code => "error." + code + ".json"))
         .Concat(ManagementSamples.FurtherSnapshots.Keys);

      string[] found = [.. Directory.GetFiles(GoldenDirectory).Select(path => Path.GetFileName(path)!)];

      Assert.Equal(expected.Order(StringComparer.Ordinal), found.Order(StringComparer.Ordinal));
      Assert.Equal((30 * 2) + 5 + 2, found.Length);
   }



   /// <summary>Each operation's golden request passes the strict envelope check and the routing check, names a server exactly when the operation acts on one, has arguments exactly when the operation takes them, and comes back as the same document after its arguments are read strictly into the operation's record and written again.</summary>
   [Theory]
   [MemberData(nameof(OperationNames))]
   public void GoldenRequest_RoundTripsThroughTheOperationsArguments(string name)
   {
      Assert.True(ManagementOperations.TryFind(name, out ManagementOperationInfo? operation));
      string golden = Normalized(name + ".request.json");

      ManagementRequestParse parse = ManagementJson.ParseRequest(golden);

      Assert.Equal(ManagementEnvelopeProblem.None, parse.Problem);
      Assert.True(parse.Succeeded());
      Assert.Null(ManagementOperations.Check(parse.Request, out ManagementOperationInfo? routed));
      Assert.Same(operation, routed);
      Assert.Equal(operation.Scope == ManagementScope.Server, parse.Request.Server is not null);
      Assert.Equal(operation.ArgumentsType is not null, parse.Request.Args is not null);
      object? arguments = operation.ArgumentsType is null ? null : ReadTyped(nameof(ManagementJson.TryReadArguments), operation.ArgumentsType, parse.Request);
      Assert.Equal(golden, ManagementJson.Write(ManagementRequest.For(parse.Request.Op, parse.Request.Server, arguments is null ? null : Payload(arguments, operation.ArgumentsType!))));
   }



   /// <summary>Each operation's golden request is the example the tests build in code, so the files and the examples cannot drift apart.</summary>
   [Theory]
   [MemberData(nameof(OperationNames))]
   public void GoldenRequest_IsTheExampleRequest(string name)
   {
      Assert.True(ManagementOperations.TryFind(name, out ManagementOperationInfo? operation));
      object? arguments = ManagementSamples.Arguments(name);
      var example = ManagementRequest.For(name, ManagementSamples.ServerFor(operation), arguments is null ? null : Payload(arguments, operation.ArgumentsType!));

      Assert.Equal(arguments?.GetType(), operation.ArgumentsType);
      Assert.Equal(Normalized(name + ".request.json"), ManagementJson.Write(example));
   }



   /// <summary>Each operation's golden reply is a success whose result comes back as the same document after it is read into the operation's result record and written again.</summary>
   [Theory]
   [MemberData(nameof(OperationNames))]
   public void GoldenReply_RoundTripsThroughTheOperationsResult(string name)
   {
      Assert.True(ManagementOperations.TryFind(name, out ManagementOperationInfo? operation));
      string golden = Normalized(name + ".reply.json");

      ManagementReplyParse parse = ManagementJson.ParseReply(golden);

      Assert.Equal(ManagementEnvelopeProblem.None, parse.Problem);
      Assert.True(parse.Succeeded());
      Assert.True(parse.Reply.Ok);
      Assert.Null(parse.Reply.Error);
      object result = ReadTyped(nameof(ManagementJson.TryReadResult), operation.ResultType, parse.Reply);
      Assert.Equal(golden, ManagementJson.Write(ManagementReply.Success(Payload(result, operation.ResultType))));
   }



   /// <summary>Each operation's golden reply is the example result the tests build in code, written by the same call the service will use for a success.</summary>
   [Theory]
   [MemberData(nameof(OperationNames))]
   public void GoldenReply_IsTheExampleResult(string name)
   {
      Assert.True(ManagementOperations.TryFind(name, out ManagementOperationInfo? operation));
      object result = ManagementSamples.Result(name);

      Assert.Equal(operation.ResultType, result.GetType());
      Assert.Equal(Normalized(name + ".reply.json"), ManagementJson.Write(SuccessOf(result)));
   }



   /// <summary>Each golden failure carries its code, a message and details that come back as the same document after they are read into the type the code names and written again; and it is the example the tests build in code.</summary>
   [Theory]
   [MemberData(nameof(CodesWithDetails))]
   public void GoldenFailure_RoundTripsThroughTheCodesDetails(string code)
   {
      Type detailsType = ManagementErrorCode.DetailsType(code)!;
      string golden = Normalized("error." + code + ".json");

      ManagementReplyParse parse = ManagementJson.ParseReply(golden);

      Assert.True(parse.Succeeded());
      Assert.False(parse.Reply.Ok);
      Assert.Null(parse.Reply.Result);
      Assert.Equal(code, parse.Reply.Error!.Code);
      Assert.Equal(ManagementSamples.FailureMessage, parse.Reply.Error.Message);
      object details = ReadTyped(nameof(ManagementJson.TryReadDetails), detailsType, parse.Reply.Error);
      Assert.Equal(golden, ManagementJson.Write(ManagementSamples.FailureWith(code, details)));
      Assert.Equal(detailsType, ManagementSamples.Details(code)!.GetType());
      Assert.Equal(golden, ManagementJson.Write(ManagementSamples.Failure(code)));
   }



   /// <summary>The further snapshot examples (a healthy service with two servers, and one whose stored data is not ready, which has none) are successes that read as a snapshot, come back as the same document, and are the examples the tests build in code.</summary>
   [Theory]
   [InlineData("snapshot.healthy.reply.json", 2, StoredState.Ready)]
   [InlineData("snapshot.degraded.reply.json", 0, StoredState.NewerBuild)]
   public void FurtherSnapshot_RoundTripsAndIsTheExample(string fileName, int servers, string state)
   {
      string golden = Normalized(fileName);

      ManagementReplyParse parse = ManagementJson.ParseReply(golden);

      Assert.True(parse.Succeeded());
      Assert.True(ManagementJson.TryReadResult(parse.Reply, out ServiceSnapshot? snapshot));
      Assert.Equal((servers, state), (snapshot.Servers.Count, snapshot.Computer.State.Status));
      Assert.Equal(golden, ManagementJson.Write(ManagementReply.Success(snapshot)));
      Assert.Equal(golden, ManagementJson.Write(ManagementReply.Success(ManagementSamples.FurtherSnapshots[fileName])));
   }



   /// <summary>A record written as the payload an envelope carries, for a type known only at run time.</summary>
   private static ManagementPayload Payload(object value, Type type) => ManagementPayload.FromElement(JsonSerializer.SerializeToElement(value, type, ManagementJson.Options));



   /// <summary>A golden file's text with its indentation removed and nothing else changed.</summary>
   private static string Normalized(string fileName)
   {
      using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenDirectory, fileName)));
      return JsonSerializer.Serialize(document.RootElement);
   }



   /// <summary>Calls one of the contract's typed readers for a type known only at run time, and requires it to succeed.</summary>
   private static object ReadTyped(string reader, Type type, object source)
   {
      object?[] arguments = [source, null];
      MethodInfo method = typeof(ManagementJson).GetMethod(reader)!.MakeGenericMethod(type);

      Assert.True((bool)method.Invoke(null, arguments)!, $"{reader} refused the golden document.");
      return arguments[1]!;
   }



   /// <summary>Builds a successful reply with <see cref="ManagementReply.Success{TResult}(TResult)"/> for a result whose type is known only at run time.</summary>
   private static ManagementReply SuccessOf(object result)
      => (ManagementReply)typeof(ManagementReply).GetMethods().Single(method => method.IsGenericMethodDefinition && string.Equals(method.Name, nameof(ManagementReply.Success), StringComparison.Ordinal)).MakeGenericMethod(result.GetType()).Invoke(null, [result])!;
}
