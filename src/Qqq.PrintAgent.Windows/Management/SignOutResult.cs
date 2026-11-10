/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The result of <see cref="ManagementOperations.SessionSignOut"/>: whether session data that could not be read was set
/// aside for recovery (the previous session should then be revoked on the server), and the identity provider's sign-out
/// request when the caller asked for one and the server offers it. This is the one reply that carries something sensitive:
/// the provider's request includes its ID-token hint, for the companion to open in the browser. It is never logged, and the
/// text form of this record is fixed.
/// </summary>
public sealed record SignOutResult(bool UnreadableSessionSetAside, QqqProviderLogout? ProviderLogout = null)
{
   /// <summary>Returns a fixed label, so logging the result can never reveal the provider's sign-out request.</summary>
   public override string ToString() => "Sign-out result (provider request not shown)";
}
