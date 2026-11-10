/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// One shortfall found while checking a server: what kind it is and a plain sentence fragment saying exactly what was
/// wrong. Text taken from the server is cut to a short printable form first, so a hostile value cannot inject lines into
/// a log or flood a screen, and no detail ever contains a credential.
/// </summary>
public sealed record QqqCompatibilityProblem(QqqCompatibilityIssue Issue, string Detail)
{
   /// <summary>The check this problem belongs to, so a wizard can mark the earlier checks as passed.</summary>
   public QqqCompatibilityStage Stage => Issue switch
   {
      QqqCompatibilityIssue.ServerUnreachable => QqqCompatibilityStage.Connection,
      QqqCompatibilityIssue.SecureConnectionFailed => QqqCompatibilityStage.Security,
      QqqCompatibilityIssue.PrintingExtensionMissing or QqqCompatibilityIssue.PrintingExtensionTooOld or QqqCompatibilityIssue.PrintingExtensionUnsupported => QqqCompatibilityStage.Printing,
      QqqCompatibilityIssue.SignInUnavailable => QqqCompatibilityStage.SignIn,
      _ => QqqCompatibilityStage.Server,
   };



   /// <summary>
   /// Makes server-supplied text safe to show: control characters become spaces and anything past 80 characters is cut,
   /// with a missing value shown as such. A version or path is never legitimately longer or multi-line.
   /// </summary>
   internal static string Printable(string? value)
   {
      if(string.IsNullOrEmpty(value))
      { return "(missing)"; }
      string cleaned = new([.. value.Take(80).Select(character => char.IsControl(character) ? ' ' : character)]);
      return value.Length > 80 ? cleaned + "..." : cleaned;
   }
}
