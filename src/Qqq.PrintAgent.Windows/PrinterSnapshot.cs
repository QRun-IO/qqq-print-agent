/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// A locally discovered queue, whether its owner has shared it with the server, and what the connected server has said about
/// it (<see cref="ServerState"/>, one of the <see cref="PrinterServerState"/> values).
/// </summary>
public sealed record PrinterSnapshot(string PrinterId, string Name, string DriverName, string PortName, string Status, string[] Formats, bool Exposed,
   string? DisplayName = null, string? Description = null, string? Location = null,
   IReadOnlyList<Qqq.PrintAgent.Core.Printing.PrinterMediaProfile>? MediaProfiles = null, string ServerState = PrinterServerState.NotChecked);

