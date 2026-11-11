/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The result of <see cref="ManagementOperations.Snapshot"/>: the whole state in one document, the computer once and every
/// server once in the operator's order, so a client asks once for everything instead of once per server and per view. It
/// carries no jobs (they are paged, per server, by <see cref="ManagementOperations.JobsQuery"/>) and no log lines. It is
/// what the service already knows: taking a snapshot never starts printer discovery or contacts a server. While the
/// stored data is not ready (see <see cref="StoredState"/>) the service runs no connections and the list of servers is
/// empty; the computer's part says why.
/// </summary>
public sealed record ServiceSnapshot(ComputerSnapshot Computer, IReadOnlyList<ServerSnapshot> Servers);
