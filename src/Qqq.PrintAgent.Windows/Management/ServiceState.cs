/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>The running service as it describes itself: the product version it was built as and when this process started, so a client can tell a service that was just restarted or is of another build than itself.</summary>
public sealed record ServiceState(string Version, DateTimeOffset StartedAt);
