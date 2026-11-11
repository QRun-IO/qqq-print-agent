/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The result of <see cref="ManagementOperations.TransportGet"/>: for every server, in the operator's order, its transport
/// state with the traffic meter and its last contact. It is the part of the snapshot that changes every second, on its
/// own, so a client can draw the meter without asking for everything else each time. While the stored data is not ready
/// the service runs no connections and the list is empty: the operation still answers, because a client repeats it
/// every second and an error every second would only be noise beside the snapshot that already says why.
/// </summary>
public sealed record TransportResult(IReadOnlyList<ServerTransport> Servers);
