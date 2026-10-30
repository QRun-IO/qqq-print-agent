/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>A locally discovered queue and whether its owner has exposed it to QQQ.</summary>
public sealed record PrinterSnapshot(string PrinterId, string Name, string DriverName, string PortName, string Status, string[] Formats, bool Exposed,
   string? DisplayName = null, string? Description = null, string? Location = null);

