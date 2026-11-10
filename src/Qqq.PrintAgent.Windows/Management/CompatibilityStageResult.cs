/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// One of the five checks a server passes through, and how it went: passed, failed with the reasons, or not reached
/// because an earlier check failed. The reasons are the sentences Core's compatibility check produced, which are already
/// printable and bounded however hostile the server's answers were. A client treats a stage or an outcome it does not
/// know as unknown and shows the stages it does know.
/// </summary>
public sealed record CompatibilityStageResult(string Stage, string Outcome, IReadOnlyList<string> Problems)
{
   /// <summary>Stage: reaching the address at all.</summary>
   public const string Connection = "connection";



   /// <summary>Stage: establishing a trusted secure connection.</summary>
   public const string Security = "security";



   /// <summary>Stage: finding a QQQ server with the API this agent needs.</summary>
   public const string Server = "server";



   /// <summary>Stage: finding a printing extension this agent can print through.</summary>
   public const string Printing = "printing";



   /// <summary>Stage: finding a sign-in method this agent implements.</summary>
   public const string SignIn = "signIn";



   /// <summary>Outcome: the check passed.</summary>
   public const string Passed = "passed";



   /// <summary>Outcome: the check failed; <see cref="Problems"/> says why.</summary>
   public const string Failed = "failed";



   /// <summary>Outcome: the check was not made, because an earlier one failed and it would mean nothing.</summary>
   public const string NotReached = "notReached";



   /// <summary>The five stages in the order they are checked and shown.</summary>
   public static IReadOnlyList<string> Order { get; } = [Connection, Security, Server, Printing, SignIn];
}
