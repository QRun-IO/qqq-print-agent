/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>Queries durable local history; search matches the start of a job ID, title or printer name.</summary>
public sealed record JobQuery(string? Search = null, string? Status = null, string? PrinterId = null,
   bool NeedsAttentionOnly = false, int Offset = 0, int PageSize = 50, string SortBy = "createdAt", bool Descending = true);
