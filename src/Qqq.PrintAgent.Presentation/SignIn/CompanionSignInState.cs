/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>
/// Where a <see cref="CompanionSignIn"/> is, or how its last step ended. It moves between these values and nothing
/// else, so a wizard step can show the right thing for each and a test can assert the path taken. Whether a session
/// is held, which is what another attempt needs, is said by <see cref="CompanionSignIn.HasSession"/>.
/// </summary>
public enum CompanionSignInState
{
   /// <summary>No session is held: nothing was prepared yet, or the session was discarded.</summary>
   NotPrepared,

   /// <summary>The server's sign-in description is being read as the signed-in Windows user.</summary>
   Preparing,

   /// <summary>The description was read and a session is held; <see cref="CompanionSignIn.Offer"/> says which methods can be used.</summary>
   Ready,

   /// <summary>The description was read, and no method is one that both the service's check and this read found. No session is kept.</summary>
   NothingOffered,

   /// <summary>The description names another sign-in provider than the service's check reported, so the server has to be checked again before anyone signs in. No session is kept.</summary>
   ProviderChanged,

   /// <summary>This Windows account refused the certificate the server presented, which the service's check had accepted; <see cref="CompanionSignIn.Rejection"/> holds the certificate and who presented it.</summary>
   AccountRefusedCertificate,

   /// <summary>The description could not be read; <see cref="CompanionSignIn.Failure"/> says why. No session is kept.</summary>
   PrepareFailed,

   /// <summary>A user name and password are being checked by the server.</summary>
   SigningIn,

   /// <summary>A browser sign-in is running: the provider's page is open, or about to be, and its callback is awaited.</summary>
   WaitingForBrowser,

   /// <summary>The server accepted the user and the session credential was returned to the caller. The session is held until it is discarded.</summary>
   SignedIn,

   /// <summary>The password sign-in was not completed; <see cref="CompanionSignIn.Failure"/> gives the reason, which never says which part was wrong. The session is kept for another attempt.</summary>
   PasswordFailed,

   /// <summary>The operator cancelled the browser sign-in. The session is kept for another attempt when the callback had not yet been handed to it, and discarded otherwise.</summary>
   BrowserCanceled,

   /// <summary>The browser sign-in was given up after its time limit. The session is kept for another attempt when the callback had not yet been handed to it, and discarded otherwise.</summary>
   BrowserTimedOut,

   /// <summary>The browser sign-in was not completed; <see cref="CompanionSignIn.Failure"/> gives the reason. The session is kept for another attempt.</summary>
   BrowserFailed,

   /// <summary>This Windows account refused the certificate the sign-in provider presented; <see cref="CompanionSignIn.Rejection"/> holds the certificate and who presented it.</summary>
   ProviderCertificateRefused,
}
