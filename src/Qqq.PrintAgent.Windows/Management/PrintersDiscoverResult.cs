/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The result of <see cref="ManagementOperations.PrintersDiscover"/>: this computer's Windows printers as the discovery
/// this request started or joined found them, and when it finished. Requests that arrived while one discovery was
/// running all carry that discovery's list and time. It is the same list and the same time the next snapshot reports
/// in its computer part, so a client that asked here does not have to wait for one.
/// </summary>
public sealed record PrintersDiscoverResult(IReadOnlyList<ComputerPrinter> Printers, DateTimeOffset DiscoveredAt);
