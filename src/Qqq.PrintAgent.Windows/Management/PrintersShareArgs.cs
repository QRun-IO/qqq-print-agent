/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>Arguments of <see cref="ManagementOperations.PrintersShare"/>: the Windows printer and whether it is shared with this server.</summary>
public sealed record PrintersShareArgs(string PrinterId, bool Shared);
