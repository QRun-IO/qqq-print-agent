/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Machine-wide diagnostic limits. Daily rotation is always on. A day's file that reaches <see cref="MaxFileMegabytes"/> rolls
/// over to a numbered segment and logging continues in a fresh file; only the newest <see cref="SegmentsPerDay"/> rolled
/// segments of a day are kept, and whole days older than <see cref="RetentionDays"/> are removed. <see cref="DebugLogging"/>
/// adds request-level detail to the shared log. <see cref="CurrentFileBytes"/> and <see cref="SegmentsToday"/> are read-only
/// observations for the Settings page.
/// </summary>
public sealed record LogSettings(int RetentionDays = 30, int MaxFileMegabytes = 100, int SegmentsPerDay = 10, bool DebugLogging = false,
   long CurrentFileBytes = 0, int SegmentsToday = 0);
