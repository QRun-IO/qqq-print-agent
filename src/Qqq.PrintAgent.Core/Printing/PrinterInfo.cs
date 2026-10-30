/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

public sealed record PrinterInfo(string PrinterId, string Name, string DriverName, string PortName, string Status, string[] Formats, string? DisplayName = null, string? Description = null, string? Location = null);


