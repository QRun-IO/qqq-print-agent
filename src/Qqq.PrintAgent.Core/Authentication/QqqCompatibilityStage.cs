/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// The checks a server passes through, in the order they are made, so a wizard can show which steps succeeded before the
/// one that failed. Each later stage is only meaningful when every earlier one passed.
/// </summary>
public enum QqqCompatibilityStage
{
   /// <summary>Reaching the address at all.</summary>
   Connection,

   /// <summary>Establishing a trusted secure connection.</summary>
   Security,

   /// <summary>Reading a QQQ discovery document with the API version, features and channel this agent needs.</summary>
   Server,

   /// <summary>Finding a printing extension this agent can print through.</summary>
   Printing,

   /// <summary>Finding a sign-in method this agent implements.</summary>
   SignIn,
}
