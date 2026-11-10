/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>The result of <see cref="ManagementOperations.SessionSignIn"/>: who is signed in, whether this computer is paired with the server, and whether printing is enabled. Signed in without printing enabled means a pairing or an approval is still needed, and the authentication message says which.</summary>
public sealed record SignInResult(AgentAuthenticationStatus Authentication, bool Paired, bool PrintingEnabled);
