/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The result of <see cref="ManagementOperations.SessionSignOut"/>. Sign-out always finishes on this computer: the stored
/// credential is removed and printing for the server stops. <see cref="ServerSessionEnded"/> says whether the session was
/// also ended on the server; when it could not be (the server did not answer, or refused), the operator should revoke it
/// there. <see cref="UnreadableSessionSetAside"/> says that session data which could not be read was set aside for
/// recovery, which also means the previous session should be revoked on the server. The provider's sign-out request is
/// present when the caller asked for one and the server offers it. That makes this the one reply that carries something
/// sensitive: the request includes the provider's ID-token hint, for the companion to open in the browser. It is never
/// logged, and the text form of this record is fixed.
/// </summary>
public sealed record SignOutResult(bool ServerSessionEnded, bool UnreadableSessionSetAside, QqqProviderLogout? ProviderLogout = null)
{
   /// <summary>Returns a fixed label, so logging the result can never reveal the provider's sign-out request.</summary>
   public override string ToString() => "Sign-out result (provider request not shown)";
}
