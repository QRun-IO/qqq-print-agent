/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>The result of <see cref="ManagementOperations.LogsClear"/>: how many activity log files were deleted. Job history and pairings are not logs and are kept.</summary>
public sealed record LogsClearResult(int DeletedFiles);
