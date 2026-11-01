/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

public sealed record JobPage(IReadOnlyList<JobSnapshot> Jobs, int TotalCount, int Offset, int PageSize, bool ServerQueueLimited = false);
