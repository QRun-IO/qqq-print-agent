/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>Retained local evidence for a job, independent of physical output confirmation.</summary>
public sealed record JobDetailsSnapshot(JobSnapshot Job, IReadOnlyList<JobEventSnapshot> Events, bool HistoryPruned);
