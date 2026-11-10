/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>The result of <see cref="ManagementOperations.ServersList"/> and <see cref="ManagementOperations.ServersReorder"/>: the servers in the operator's order.</summary>
public sealed record ServerListResult(IReadOnlyList<ServerSummary> Servers);
