/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>Local observations of the cloud transport. Totals span this service lifetime, including reconnects.
/// Bytes include HTTP bodies, streamed documents and WebSocket JSON payloads, but exclude headers, framing and TLS.</summary>
public sealed record AgentTransportSnapshot(string Protocol, string State, long UploadedBytes, long DownloadedBytes,
   double UploadBytesPerSecond, double DownloadBytesPerSecond, DateTimeOffset TotalsSinceUtc);
