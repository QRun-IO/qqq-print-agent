/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// What is true of this computer whatever servers it has: the Windows printers that exist, the running service, the update
/// pause, the log limits, whether the stored data is usable, and the computer's own name, description and location, which
/// every server is given alike.
/// </summary>
public sealed record ComputerSnapshot(IReadOnlyList<ComputerPrinter> Printers, ServiceState Service, UpdateState Update, LogSettings LogSettings, StoredState State, DeviceProfile Profile);
