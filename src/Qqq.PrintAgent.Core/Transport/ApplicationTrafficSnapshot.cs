/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Transport;

/// <summary>A point-in-time reading of application bytes sent and received, with current rates and the time totals began, so the UI shows traffic this agent itself generated rather than adapter-wide numbers.</summary>
public sealed record ApplicationTrafficSnapshot(long UploadedBytes, long DownloadedBytes, double UploadBytesPerSecond, double DownloadBytesPerSecond, DateTimeOffset TotalsSinceUtc);
