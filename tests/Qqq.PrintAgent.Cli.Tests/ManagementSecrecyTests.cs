/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Reflection;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// No reply carries a session credential, a device token, an enrollment code or document content. Two tests hold that
/// from two sides. The first is structural: it walks every type a result or an error's details can contain and requires
/// each member to be of a kind that cannot hold a secret by accident, so there is no free-form bag and no credential type
/// anywhere in a reply. The second serializes a fully populated example of every result, built from secret-bearing
/// objects that carry canaries, and requires the canaries to be absent. One exception is named in ADR-0004 and held here
/// to exactly one place: the sign-out result carries the identity provider's sign-out request.
/// </summary>
public sealed class ManagementSecrecyTests
{
   /// <summary>The records a reply may be made of besides the contract's own: existing snapshot records the contract reuses, each already free of credentials.</summary>
   private static readonly Type[] s_reusedRecords =
   [
      typeof(JobPage), typeof(JobSnapshot), typeof(JobDetailsSnapshot), typeof(JobEventSnapshot), typeof(LogSettings), typeof(AgentAuthenticationStatus), typeof(AgentTransportSnapshot),
      typeof(PrinterServerSync), typeof(DeviceProfile), typeof(DeviceDesiredState), typeof(PrinterMediaProfile),
   ];

   /// <summary>The kinds of value a member of a reply may have. Text is bounded by whoever produces it; none of these can hold a credential object or arbitrary JSON.</summary>
   private static readonly Type[] s_plainValues = [typeof(string), typeof(bool), typeof(int), typeof(long), typeof(double), typeof(DateTimeOffset)];

   /// <summary>Types that hold a secret, or could hold anything: none may appear in a reply.</summary>
   private static readonly Type[] s_forbidden =
   [
      typeof(QqqSessionCredential), typeof(QqqDeviceCredential), typeof(AgentConfiguration), typeof(AgentAuthentication), typeof(EnrollmentResponse), typeof(EnrollmentRequest),
      typeof(JsonElement), typeof(JsonDocument), typeof(object), typeof(byte[]),
   ];



   /// <summary>Every member of every result type and every details type, however deeply nested, is plain text, a number, a boolean, a date, a list of those, or another record of the contract or of the reused list. The one member that is none of these is the provider's sign-out request on the sign-out result.</summary>
   [Fact]
   public void Replies_AreMadeOnlyOfPlainValuesAndKnownRecords()
   {
      List<string> exceptions = [];
      HashSet<Type> visited = [];
      IEnumerable<Type> roots = ManagementOperations.All.Select(operation => operation.ResultType)
         .Concat(ManagementErrorCode.All.Select(ManagementErrorCode.DetailsType).OfType<Type>());

      foreach(Type root in roots)
      {
         Walk(root, visited, exceptions);
      }

      Assert.Equal(["SignOutResult.ProviderLogout: QqqProviderLogout"], exceptions);
      Assert.DoesNotContain(visited, type => s_forbidden.Contains(type));
      Assert.Contains(typeof(ServerSnapshot), visited);
      Assert.Contains(typeof(PresentedCertificate), visited);
      Assert.Contains(typeof(JobEventSnapshot), visited);
   }



   /// <summary>No argument record is used as a result and no result as an argument, so a credential that travels to the service in a request has no type in common with anything that travels back.</summary>
   [Fact]
   public void ArgumentRecords_AreNeverResults()
   {
      Type[] arguments = [.. ManagementOperations.All.Select(operation => operation.ArgumentsType).OfType<Type>()];
      HashSet<Type> visited = [];
      foreach(ManagementOperationInfo operation in ManagementOperations.All)
      {
         Walk(operation.ResultType, visited, []);
      }

      Assert.Empty(arguments.Intersect(visited));
      Assert.Equal(18, arguments.Length);
   }



   /// <summary>
   /// Every operation's example result, written as the service will write it, contains none of the canaries that stand for
   /// the session cookie, the device token and the enrollment code. The examples are built from a session credential, a
   /// stored pairing, a stored session and an enrollment answer that each carry a canary where the real one carries its
   /// secret, so a result that copied one would show it. The same holds for the example details of every error code.
   /// </summary>
   [Fact]
   public void NoReply_ContainsACredentialCanary()
   {
      List<string> written = [];
      foreach(ManagementOperationInfo operation in ManagementOperations.All)
      {
         object result = ManagementSamples.Result(operation.Name);
         written.Add(ManagementJson.Write(new ManagementReply(ManagementProtocol.Version, true, JsonSerializer.SerializeToElement(result, operation.ResultType, ManagementJson.Options))));
         written.Add(result.ToString()!);
      }
      foreach(string code in ManagementErrorCode.All)
      {
         object? details = ManagementSamples.Details(code);
         written.Add(ManagementJson.Write(new ManagementReply(ManagementProtocol.Version, false, Error: new ManagementError(code, "Synthetic message for people.",
            details is null ? null : JsonSerializer.SerializeToElement(details, details.GetType(), ManagementJson.Options)))));
      }

      Assert.Equal(28 + 28 + 26, written.Count);
      Assert.All(written, text => Assert.DoesNotContain(ManagementSamples.SessionCookieCanary, text, StringComparison.Ordinal));
      Assert.All(written, text => Assert.DoesNotContain(ManagementSamples.DeviceTokenCanary, text, StringComparison.Ordinal));
      Assert.All(written, text => Assert.DoesNotContain(ManagementSamples.EnrollmentCodeCanary, text, StringComparison.Ordinal));
   }



   /// <summary>The canaries really are where the secrets are: the examples' sources hold them, and the requests that carry a credential or a code to the service contain them. Without this the test above could pass because the canaries were never there.</summary>
   [Fact]
   public void Canaries_AreInTheSourcesAndInTheRequestsThatCarryThem()
   {
      Assert.Equal(ManagementSamples.SessionCookieCanary, ManagementSamples.SessionCredential.SessionToken);
      Assert.Equal(ManagementSamples.SessionCookieCanary, ManagementSamples.StoredSession.Credential!.SessionToken);
      Assert.Equal(ManagementSamples.DeviceTokenCanary, ManagementSamples.StoredPairing.DeviceToken);
      Assert.Equal(ManagementSamples.DeviceTokenCanary, ManagementSamples.EnrollmentAnswer.DeviceToken);
      Assert.Contains(ManagementSamples.SessionCookieCanary, RequestText(ManagementOperations.ServersAdd), StringComparison.Ordinal);
      Assert.Contains(ManagementSamples.SessionCookieCanary, RequestText(ManagementOperations.SessionSignIn), StringComparison.Ordinal);
      Assert.Contains(ManagementSamples.EnrollmentCodeCanary, RequestText(ManagementOperations.PairingEnroll), StringComparison.Ordinal);
   }



   /// <summary>The provider's sign-out request, with its ID-token hint, is in the sign-out reply and in no other reply, and the sign-out result never shows it as text.</summary>
   [Fact]
   public void ProviderSignOutRequest_IsInTheSignOutReplyAndNowhereElse()
   {
      string[] carrying = [.. ManagementOperations.All.Where(operation => ManagementJson.Write(new ManagementReply(ManagementProtocol.Version, true,
         JsonSerializer.SerializeToElement(ManagementSamples.Result(operation.Name), operation.ResultType, ManagementJson.Options))).Contains(ManagementSamples.IdTokenHintCanary, StringComparison.Ordinal))
         .Select(operation => operation.Name)];

      Assert.Equal([ManagementOperations.SessionSignOut], carrying);
      Assert.DoesNotContain(ManagementSamples.IdTokenHintCanary, ManagementSamples.Result(ManagementOperations.SessionSignOut).ToString(), StringComparison.Ordinal);
      Assert.DoesNotContain(ManagementSamples.IdTokenHintCanary, ManagementReply.Success((SignOutResult)ManagementSamples.Result(ManagementOperations.SessionSignOut)).ToString(), StringComparison.Ordinal);
   }



   /// <summary>The example request of an operation as the wire carries it.</summary>
   private static string RequestText(string operation)
   {
      object arguments = ManagementSamples.Arguments(operation)!;
      return ManagementJson.Write(new ManagementRequest(ManagementProtocol.Version, operation, null, JsonSerializer.SerializeToElement(arguments, arguments.GetType(), ManagementJson.Options)));
   }



   /// <summary>Visits a record's public properties, descending into records and list elements; a property whose type is neither a plain value nor an allowed record is noted as an exception with the record and member it is on.</summary>
   private static void Walk(Type type, HashSet<Type> visited, List<string> exceptions)
   {
      if(!visited.Add(type))
      {
         return;
      }
      foreach(PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(candidate => !string.Equals(candidate.Name, "EqualityContract", StringComparison.Ordinal)))
      {
         Type member = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
         Type element = member.IsArray ? member.GetElementType()! : member.IsGenericType && member.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ? member.GetGenericArguments()[0] : member;
         if(s_plainValues.Contains(element))
         {
            continue;
         }
         if(s_reusedRecords.Contains(element) || string.Equals(element.Namespace, typeof(ManagementJson).Namespace, StringComparison.Ordinal))
         {
            Walk(element, visited, exceptions);
            continue;
         }
         exceptions.Add($"{type.Name}.{property.Name}: {element.Name}");
      }
   }
}
