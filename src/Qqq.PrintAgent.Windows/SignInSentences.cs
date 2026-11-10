/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The sentences the service uses to say what a sign-in achieved. Until the management pipe has typed replies, the
/// companion reads the service's sign-in answer by these sentences, so the service that writes them and the companion
/// that recognises them take them from this one place and cannot drift apart.
/// </summary>
public static class SignInSentences
{
   /// <summary>How the service's own status message opens once a session is held.</summary>
   public const string SignedIn = "Signed in.";

   /// <summary>What follows the sign-in opening when the device is paired and bound to the account, so printing works.</summary>
   public const string PrintingEnabled = " Printing is enabled.";

   /// <summary>What follows the sign-in opening when the account is signed in but this computer still has to be enrolled.</summary>
   public const string EnrollmentNeeded = " Enroll this computer to enable printing.";

   /// <summary>The service's status message for a session without a paired device: the opening followed by the enrollment sentence.</summary>
   public const string SignedInEnrollmentNeeded = SignedIn + EnrollmentNeeded;
}
