/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>The result of <see cref="ManagementOperations.ServersAdd"/>: the server as it was stored, with the identifier it was given, and who its session is signed in as.</summary>
public sealed record ServerAddResult(ServerSummary Server, AgentAuthenticationStatus Authentication);
