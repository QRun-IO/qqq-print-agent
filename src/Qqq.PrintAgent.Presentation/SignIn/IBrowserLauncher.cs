/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>Opens an address in the user's default browser. The address can carry a sign-in state or a token hint, so implementations hand it straight to the browser and never log it.</summary>
public interface IBrowserLauncher
{
   /// <summary>Opens the address; a failure to start the browser is thrown to the caller.</summary>
   void Open(Uri address);
}
