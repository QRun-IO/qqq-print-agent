/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>Machine-wide diagnostic limits. Daily rotation is always enabled.</summary>
public sealed record LogSettings(int RetentionDays = 30, int MaxDailyMegabytes = 20, long CurrentDayBytes = 0, bool DailyLimitReached = false);
