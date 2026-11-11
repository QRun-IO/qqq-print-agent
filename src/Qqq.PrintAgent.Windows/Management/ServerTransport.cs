/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>One server's transport state with its traffic meter, and when the server last answered this computer: the same two members a <see cref="ServerSnapshot"/> carries, keyed by the server's identifier.</summary>
public sealed record ServerTransport(string ServerId, AgentTransportSnapshot Transport, DateTimeOffset? LastContact = null);
