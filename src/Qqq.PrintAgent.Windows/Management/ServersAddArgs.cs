/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// Arguments of <see cref="ManagementOperations.ServersAdd"/>: the address, the name, whether the operator accepted the
/// certificate exception, and the session from a sign-in the companion has completed. The sign-in provider's address is
/// not an argument: the service reads it from the server itself and never takes it from a client. The session credential
/// is a secret, so the text form of these arguments is fixed.
/// </summary>
public sealed record ServersAddArgs(string Address, string Name, bool AcceptCertificateException, QqqSessionCredential SessionCredential)
{
   /// <summary>Returns a fixed label, so logging the arguments can never reveal the session credential.</summary>
   public override string ToString() => "Arguments of servers.add (session credential not shown)";
}
