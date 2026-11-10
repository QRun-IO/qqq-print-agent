/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Reflection;
using Qqq.PrintAgent.Windows;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The two tables of the management contract, held against the lists in ADR-0004 written out here. A code or an
/// operation cannot be added, removed, renamed or moved between the computer and a server without this file changing
/// too, so such a change is always a deliberate one.
/// </summary>
public sealed class ManagementTableTests
{
   /// <summary>The error codes exactly as ADR-0004's table spells them, in its order.</summary>
   private static readonly string[] s_codesInTheAdr =
   [
      "unsupported_version", "unknown_operation", "invalid_request", "server_not_found", "server_ambiguous", "server_limit", "server_exists", "busy",
      "not_signed_in", "not_paired", "server_incompatible", "certificate_untrusted", "sign_in_refused", "bind_refused", "identity_changed",
      "enrollment_refused", "enrollment_unconfirmed", "printer_not_found", "job_not_found", "job_not_current", "duplicate_risk_unconfirmed",
      "state_unreadable", "state_newer", "migration_incomplete", "update_in_progress", "internal_error",
   ];

   /// <summary>ADR-0004: operations on the computer or the list of servers, which name no server.</summary>
   private static readonly string[] s_computerOperationsInTheAdr =
   [
      "snapshot", "servers.list", "servers.check", "servers.add", "servers.reorder", "computer.getProfile", "computer.setProfile",
      "logs.getSettings", "logs.setSettings", "logs.clear", "update.prepare", "update.resume",
   ];

   /// <summary>ADR-0004: operations on one server, which must name it.</summary>
   private static readonly string[] s_serverOperationsInTheAdr =
   [
      "servers.rename", "servers.remove", "session.signIn", "session.signOut", "pairing.enroll", "connection.reconnect", "trust.get", "trust.set",
      "printers.share", "printers.setProfile", "printers.testPrint", "jobs.query", "jobs.details", "jobs.retry", "jobs.cancel", "jobs.acknowledge",
   ];



   /// <summary>The code table is the ADR's, spelled and ordered as there; every constant the class declares is in it once, and nothing else is.</summary>
   [Fact]
   public void ErrorCodes_AreTheAdrsTable_SpelledAsTheAdrSpellsThem()
   {
      string[] declared = Constants(typeof(ManagementErrorCode));

      Assert.Equal(s_codesInTheAdr, ManagementErrorCode.All);
      Assert.Equal(26, ManagementErrorCode.All.Count);
      Assert.Equal(s_codesInTheAdr.Order(StringComparer.Ordinal), declared.Order(StringComparer.Ordinal));
      Assert.All(ManagementErrorCode.All, code => Assert.Matches("^[a-z]+(_[a-z]+)*$", code));
   }



   /// <summary>Exactly three codes carry details, as their table entries say: the two compatibility failures carry the check's stages, and an unconfirmed enrollment says what was left behind. Every other code, and a code that is not in the table, carries none.</summary>
   [Fact]
   public void ErrorCodes_ThatCarryDetails_AreTheThreeTheAdrNames()
   {
      Assert.Equal(typeof(ServerCheckResult), ManagementErrorCode.DetailsType(ManagementErrorCode.ServerIncompatible));
      Assert.Equal(typeof(ServerCheckResult), ManagementErrorCode.DetailsType(ManagementErrorCode.CertificateUntrusted));
      Assert.Equal(typeof(EnrollmentUnconfirmedDetails), ManagementErrorCode.DetailsType(ManagementErrorCode.EnrollmentUnconfirmed));
      Assert.Equal(3, ManagementErrorCode.All.Count(code => ManagementErrorCode.DetailsType(code) is not null));
      Assert.Null(ManagementErrorCode.DetailsType("code_added_later"));
   }



   /// <summary>The operation table holds the ADR's two lists, each in its order, the computer's first: an operation's scope is which list it is in. Every name constant the class declares is in the table once.</summary>
   [Fact]
   public void Operations_AreTheAdrsTwoLists_AndTheirScopeIsTheListTheyAreIn()
   {
      string[] computer = [.. ManagementOperations.All.Where(operation => operation.Scope == ManagementScope.Computer).Select(operation => operation.Name)];
      string[] server = [.. ManagementOperations.All.Where(operation => operation.Scope == ManagementScope.Server).Select(operation => operation.Name)];

      Assert.Equal(s_computerOperationsInTheAdr, computer);
      Assert.Equal(s_serverOperationsInTheAdr, server);
      Assert.Equal(28, ManagementOperations.All.Count);
      Assert.Equal([.. s_computerOperationsInTheAdr, .. s_serverOperationsInTheAdr], ManagementOperations.All.Select(operation => operation.Name), StringComparer.Ordinal);
      Assert.Equal(ManagementOperations.All.Select(operation => operation.Name).Order(StringComparer.Ordinal), Constants(typeof(ManagementOperations)).Order(StringComparer.Ordinal));
   }



   /// <summary>An operation is found by its exact name and no other spelling.</summary>
   [Fact]
   public void Operations_AreFoundByExactName()
   {
      Assert.True(ManagementOperations.TryFind("jobs.query", out ManagementOperationInfo? found));
      Assert.Equal((ManagementOperations.JobsQuery, ManagementScope.Server, ManagementOperationKind.Read), (found.Name, found.Scope, found.Kind));
      Assert.False(ManagementOperations.TryFind("Jobs.Query", out ManagementOperationInfo? otherCase));
      Assert.Null(otherCase);
      Assert.False(ManagementOperations.TryFind("jobs.query ", out _));
      Assert.False(ManagementOperations.TryFind("", out _));
   }



   /// <summary>The five operations that change one server's session, pairing or trust are the ones the ADR says run one at a time per server and answer busy; the operations that change nothing are reads; everything else is an ordinary change.</summary>
   [Fact]
   public void Operations_KindsFollowTheAdrsConcurrencyRules()
   {
      string[] sessionChanges = ["session.signIn", "session.signOut", "pairing.enroll", "trust.set", "servers.remove"];
      string[] reads = ["snapshot", "servers.list", "servers.check", "computer.getProfile", "logs.getSettings", "trust.get", "jobs.query", "jobs.details"];

      Assert.Equal(sessionChanges.Order(StringComparer.Ordinal), Names(operation => operation.Kind == ManagementOperationKind.SessionChange), StringComparer.Ordinal);
      Assert.Equal(reads.Order(StringComparer.Ordinal), Names(operation => operation.Kind == ManagementOperationKind.Read), StringComparer.Ordinal);
      Assert.Equal(15, ManagementOperations.All.Count(operation => operation.Kind == ManagementOperationKind.Change));
      Assert.All(ManagementOperations.All.Where(operation => operation.Kind == ManagementOperationKind.SessionChange), operation => Assert.Equal(ManagementScope.Server, operation.Scope));
   }



   /// <summary>The service gives an operation 30 seconds, and 120 to the ones that wait for a QQQ server: sign-in, sign-out and enrollment as in version 1, and checking and adding a server. A client waits a little longer than the service, 35 and 150 seconds as version 1 clients do, so it hears the service's own timeout.</summary>
   [Fact]
   public void Operations_TimeLimitsAreTodaysThirtySeconds_AndOneHundredTwentyForTheLongOnes()
   {
      string[] longOnes = ["session.signIn", "session.signOut", "pairing.enroll", "servers.check", "servers.add"];

      Assert.Equal(TimeSpan.FromSeconds(30), ManagementOperations.OrdinaryTimeLimit);
      Assert.Equal(TimeSpan.FromSeconds(120), ManagementOperations.LongTimeLimit);
      Assert.Equal(longOnes.Order(StringComparer.Ordinal), Names(operation => operation.ServerTimeLimit == ManagementOperations.LongTimeLimit), StringComparer.Ordinal);
      Assert.Equal(23, ManagementOperations.All.Count(operation => operation.ServerTimeLimit == ManagementOperations.OrdinaryTimeLimit));
      Assert.All(ManagementOperations.All, operation => Assert.Equal(
         operation.ServerTimeLimit == ManagementOperations.LongTimeLimit ? TimeSpan.FromSeconds(150) : TimeSpan.FromSeconds(35), operation.ClientTimeLimit()));
   }



   /// <summary>While the stored data is not ready (written by a newer build, or an upgrade that could not finish) only the snapshot, the log operations and the update operations answer, so the newer build can be reinstalled; no operation on a server does.</summary>
   [Fact]
   public void Operations_AvailableWhenDegraded_AreTheSnapshotTheLogsAndTheUpdates()
   {
      string[] available = ["snapshot", "logs.getSettings", "logs.setSettings", "logs.clear", "update.prepare", "update.resume"];

      Assert.Equal(available.Order(StringComparer.Ordinal), Names(operation => operation.AvailableWhenDegraded), StringComparer.Ordinal);
      Assert.All(ManagementOperations.All.Where(operation => operation.AvailableWhenDegraded), operation => Assert.Equal(ManagementScope.Computer, operation.Scope));
   }



   /// <summary>Ten operations take no arguments; each of the other eighteen has its own argument record, used by no other operation, and every operation has a result record.</summary>
   [Fact]
   public void Operations_EachHaveTheirOwnArgumentRecord_AndAResult()
   {
      string[] withoutArguments = ["snapshot", "servers.list", "computer.getProfile", "logs.getSettings", "logs.clear", "update.prepare", "update.resume", "servers.remove", "connection.reconnect", "trust.get"];
      Type[] argumentTypes = [.. ManagementOperations.All.Where(operation => operation.ArgumentsType is not null).Select(operation => operation.ArgumentsType!)];

      Assert.Equal(withoutArguments.Order(StringComparer.Ordinal), Names(operation => operation.ArgumentsType is null), StringComparer.Ordinal);
      Assert.Equal(18, argumentTypes.Distinct().Count());
      Assert.All(argumentTypes, type => Assert.EndsWith("Args", type.Name, StringComparison.Ordinal));
      Assert.All(argumentTypes, type => Assert.Equal("Qqq.PrintAgent.Windows.Management", type.Namespace));
      Assert.All(ManagementOperations.All, operation => Assert.NotNull(operation.ResultType));
   }



   /// <summary>The protocol's numbers: version 2, sixteen clients at once, eight servers, and the size limits of the existing frame, so a version 2 message fits where a version 1 message did.</summary>
   [Fact]
   public void Protocol_NumbersAreTheAdrs_AndTheSizesAreTheExistingFrames()
   {
      Assert.Equal(2, ManagementProtocol.Version);
      Assert.Equal(16, ManagementProtocol.MaximumConcurrentClients);
      Assert.Equal(8, ManagementProtocol.MaximumServers);
      Assert.Equal(AgentPipeFraming.MaximumCommandCharacters, ManagementProtocol.MaximumRequestCharacters);
      Assert.Equal(AgentPipeFraming.MaximumResponseCharacters, ManagementProtocol.MaximumReplyCharacters);
   }



   /// <summary>A server identifier has one spelling, the GUID's canonical lower-case form, and it is what a GUID is written as; every other spelling of the same GUID, and anything that is not a GUID, is not an identifier.</summary>
   [Theory]
   [InlineData("3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f", true)]
   [InlineData("00000000-0000-0000-0000-000000000000", true)]
   [InlineData("3F2B8C1E-5D4A-4F6B-9A7C-0E1D2C3B4A5F", false)]
   [InlineData("3f2b8c1e5d4a4f6b9a7c0e1d2c3b4a5f", false)]
   [InlineData("{3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f}", false)]
   [InlineData("(3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f)", false)]
   [InlineData(" 3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f", false)]
   [InlineData("3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5", false)]
   [InlineData("3f2b8c1e", false)]
   [InlineData("Warehouse", false)]
   [InlineData("", false)]
   [InlineData(null, false)]
   public void ServerIdentifier_HasOneSpelling(string? value, bool isIdentifier)
   {
      Assert.Equal(isIdentifier, ManagementProtocol.IsServerId(value));
      Assert.True(!isIdentifier || string.Equals(ManagementProtocol.FormatServerId(Guid.Parse(value!)), value, StringComparison.Ordinal));
   }



   /// <summary>The names of the operations a condition holds for, sorted.</summary>
   private static IEnumerable<string> Names(Func<ManagementOperationInfo, bool> condition)
      => ManagementOperations.All.Where(condition).Select(operation => operation.Name).Order(StringComparer.Ordinal);



   /// <summary>The values of every public text constant a class declares.</summary>
   private static string[] Constants(Type type)
      => [.. type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.IsLiteral && field.FieldType == typeof(string)).Select(field => (string)field.GetRawConstantValue()!)];
}
