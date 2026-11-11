/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>Whether claiming is paused for an update and, if so, when the pause runs out without a resume. The pause lives in memory, so a service that was just started is never paused.</summary>
public sealed record UpdateState(bool Paused, DateTimeOffset? PausedUntil = null);
