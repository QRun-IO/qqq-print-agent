/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// The ways a server lets this agent sign a user in. A server offers one of them today, but the value is a set so the
/// wizard shows whatever is offered instead of assuming.
/// </summary>
[Flags]
public enum QqqSignInMethods
{
   /// <summary>No method this agent implements.</summary>
   None = 0,

   /// <summary>A user name and password, sent once to the server's own session endpoint (table-based authentication).</summary>
   Password = 1,

   /// <summary>The system browser and the server's identity provider, through the native session grant with PKCE.</summary>
   Browser = 2,
}
