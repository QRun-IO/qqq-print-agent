/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>The submitter's choices for one job: copies, scaling, paper size and optional media selection. Defaults mean one copy at actual size, so an unspecified option never rescales a document.</summary>
public sealed record PrintOptions(int Copies = 1, string Scale = "ACTUAL_SIZE", double? PaperWidthMm = null, double? PaperHeightMm = null, PrinterMediaSelection? MediaProfile = null);


