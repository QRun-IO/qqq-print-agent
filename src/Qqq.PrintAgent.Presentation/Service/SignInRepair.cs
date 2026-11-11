/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Service;

/// <summary>
/// The repair an operator chose after a sign-in was refused with <see cref="ManagementErrorCode.BindRefused"/>: one
/// of the two the contract defines, and nothing else. It is a type and not text so that
/// <see cref="ManagementClient.SignInAsync"/> cannot be given a repair the service does not know. There are exactly
/// two values, and no other can be made.
/// </summary>
public sealed class SignInRepair
{
   /// <summary>Holds one of the contract's two values.</summary>
   private SignInRepair(string value)
   {
      Value = value;
   }



   /// <summary>Bind the existing pairing to the account that signed in, for a pairing created before accounts were linked to devices (<see cref="SessionSignInArgs.AssociatePairing"/>).</summary>
   public static SignInRepair AssociatePairing { get; } = new(SessionSignInArgs.AssociatePairing);



   /// <summary>Sign in without binding the saved pairing, so a new one can be enrolled, for a device that was revoked or replaced; history and printer choices are kept (<see cref="SessionSignInArgs.NewPairing"/>).</summary>
   public static SignInRepair NewPairing { get; } = new(SessionSignInArgs.NewPairing);



   /// <summary>The repair as the request carries it.</summary>
   internal string Value { get; }
}
