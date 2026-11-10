/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>Arguments of <see cref="ManagementOperations.JobsAcknowledge"/>: the failed job the operator has seen.</summary>
public sealed record JobsAcknowledgeArgs(string JobId);
