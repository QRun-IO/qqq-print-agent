/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>Arguments of <see cref="ManagementOperations.JobsQuery"/>: the filter, sort order and page.</summary>
public sealed record JobsQueryArgs(JobQuery Query);
