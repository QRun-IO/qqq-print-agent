/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>The result of <see cref="ManagementOperations.PrintersTestPrint"/> and <see cref="ManagementOperations.JobsRetry"/>: the server queued a new job, whose identifier is given when the server returned one. It appears in the job list when this computer receives it.</summary>
public sealed record JobQueuedResult(string? JobId = null);
