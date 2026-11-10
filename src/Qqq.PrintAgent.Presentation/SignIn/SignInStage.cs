/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>Where the connection flow is. The flow moves between these stages and nothing else, so a view can show the right step and a test can assert the path taken.</summary>
public enum SignInStage
{
   /// <summary>No server has been discovered, or the last attempt was abandoned; discovery is the only way forward.</summary>
   Idle,

   /// <summary>The server is being asked which sign-in methods it offers.</summary>
   Discovering,

   /// <summary>The server answered; the offered methods can be used.</summary>
   Ready,

   /// <summary>A user name and password are being checked by the server.</summary>
   SigningIn,

   /// <summary>The provider's page is open in the browser and the flow is waiting for its callback.</summary>
   WaitingForBrowser,

   /// <summary>The server accepted the user and the session is being handed to the service.</summary>
   HandingOver,

   /// <summary>The service confirmed the sign-in and now holds the session; the flow holds none, so another attempt starts with discovery.</summary>
   SignedIn,

   /// <summary>The service is enrolling this computer with an enrollment code.</summary>
   Enrolling,

   /// <summary>The service is ending its session, and the identity provider's sign-out may be open in the browser.</summary>
   SigningOut,
}
