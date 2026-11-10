/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>Arguments of <see cref="ManagementOperations.PrintersSetProfile"/>: the printer and the name, description and location this server is given for it.</summary>
public sealed record PrintersSetProfileArgs(string PrinterId, PrinterProfile Profile);
