/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>One page of the job list with the total count, offset and page size, plus a flag when the server queue preview was cut short, so paging controls and the "more waiting" notice stay accurate.</summary>
public sealed record JobPage(IReadOnlyList<JobSnapshot> Jobs, int TotalCount, int Offset, int PageSize, bool ServerQueueLimited = false);
