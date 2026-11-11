/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// Arguments of <see cref="ManagementOperations.LogsSetSettings"/>: the four log limits a client can set, all of them, so
/// a change never depends on what was stored before. The current file's size and today's segment count, which the
/// result also reports, are observations and are not arguments.
/// </summary>
public sealed record LogsSetSettingsArgs(int RetentionDays, int MaxFileMegabytes, int SegmentsPerDay, bool DebugLogging);
