/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Service;

/// <summary>What still has to happen after the service accepted a sign-in. The service says it in a sentence; the client reads the sentence once, so a view or a wizard branches on a value.</summary>
public enum SignInReadiness
{
   /// <summary>The device is paired and bound to the signed-in account: printing is enabled.</summary>
   PrintingEnabled,

   /// <summary>The account is signed in, but this computer still has to be enrolled before it can print.</summary>
   EnrollmentNeeded,

   /// <summary>The account is signed in and the service named another condition in its sentence.</summary>
   NotReady,
}
