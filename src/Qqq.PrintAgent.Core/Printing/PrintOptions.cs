/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

public sealed record PrintOptions(int Copies = 1, string Scale = "ACTUAL_SIZE", double? PaperWidthMm = null, double? PaperHeightMm = null, PrinterMediaSelection? MediaProfile = null);


