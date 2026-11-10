/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>Arguments of <see cref="ManagementOperations.LogsSetSettings"/>: the log limits to store. The two observations a <see cref="LogSettings"/> also carries (the current file's size and today's segment count) are ignored.</summary>
public sealed record LogsSetSettingsArgs(LogSettings Settings);
