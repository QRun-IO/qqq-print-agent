/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>Arguments of <see cref="ManagementOperations.JobsRetry"/>: the job, and the operator's confirmation that printing it again may produce a second copy. A retry that could print twice is refused with <see cref="ManagementErrorCode.DuplicateRiskUnconfirmed"/> without it.</summary>
public sealed record JobsRetryArgs(string JobId, bool ConfirmDuplicateRisk = false);
