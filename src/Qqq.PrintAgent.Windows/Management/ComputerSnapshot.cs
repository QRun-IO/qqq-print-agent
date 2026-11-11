/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// What is true of this computer whatever servers it has: the Windows printers as the service last discovered them and
/// when that was, the running service, the update pause, the log limits, whether the stored data is usable, and the
/// computer's own name, description and location, which every server is given alike. A snapshot reports the printer
/// list and never starts a discovery. The service refreshes the list at least every 60 seconds and whenever a server
/// connection synchronizes its printers, so the list is at most that old; a client that needs it fresh asks
/// <see cref="ManagementOperations.PrintersDiscover"/>. Before the first discovery the time is absent, which is how an
/// empty list that means "not looked yet" is told from a computer with no printers.
/// </summary>
public sealed record ComputerSnapshot(IReadOnlyList<ComputerPrinter> Printers, DateTimeOffset? PrintersDiscoveredAt, ServiceState Service, UpdateState Update, LogSettings LogSettings,
   StoredState State, DeviceProfile Profile);
