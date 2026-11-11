/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// One printer shared with a server: which Windows printer it is, what the server has said about it (one of the
/// <see cref="PrinterServerState"/> values), and the name, description and location this server is given for it. A printer
/// that is not shared with the server is not listed.
/// </summary>
public sealed record ServerPrinter(string PrinterId, string ServerState, string? DisplayName = null, string? Description = null, string? Location = null);
