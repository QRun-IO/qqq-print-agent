/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// Arguments of <see cref="ManagementOperations.SessionSignIn"/>: the session from a sign-in the companion has completed and,
/// only after the ordinary bind was refused, the repair the operator chose. Without a repair the saved pairing is bound as it
/// is. The two repairs exclude each other, so they are one member with two values, not two switches that could both be on.
/// The session credential is a secret, so the text form of these arguments is fixed.
/// </summary>
public sealed record SessionSignInArgs(QqqSessionCredential SessionCredential, string? Repair = null)
{
   /// <summary>Repair: bind the existing pairing to the account that signed in, for a pairing created before accounts were linked to devices.</summary>
   public const string AssociatePairing = "associatePairing";

   /// <summary>Repair: sign in without binding the saved pairing, so a new one can be enrolled, for a device that was revoked or replaced. History and printer choices are kept.</summary>
   public const string NewPairing = "newPairing";



   /// <summary>Returns a fixed label, so logging the arguments can never reveal the session credential.</summary>
   public override string ToString() => "Arguments of session.signIn (session credential not shown)";
}
