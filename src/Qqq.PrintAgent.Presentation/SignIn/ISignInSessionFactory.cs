/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>Creates the session for a new sign-in attempt, so the flow never constructs a network client itself.</summary>
public interface ISignInSessionFactory
{
   /// <summary>
   /// Creates a session for a server. It may refuse the address (for example plain HTTP to a remote host) by throwing an
   /// argument exception, which the flow reports as a failed discovery. The certificate exception applies to this one server only.
   /// </summary>
   ISignInSession Create(Uri origin, bool allowUntrustedCertificate);
}
