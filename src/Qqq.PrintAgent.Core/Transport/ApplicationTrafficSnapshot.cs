/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Transport;

public sealed record ApplicationTrafficSnapshot(long UploadedBytes, long DownloadedBytes, double UploadBytesPerSecond, double DownloadBytesPerSecond, DateTimeOffset TotalsSinceUtc);
