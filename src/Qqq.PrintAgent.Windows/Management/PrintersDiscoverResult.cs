/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The result of <see cref="ManagementOperations.PrintersDiscover"/>: this computer's Windows printers as a discovery run
/// for this request found them, and when it finished. It is the same list and the same time the next snapshot reports
/// in its computer part, so a client that asked here does not have to wait for one.
/// </summary>
public sealed record PrintersDiscoverResult(IReadOnlyList<ComputerPrinter> Printers, DateTimeOffset DiscoveredAt);
