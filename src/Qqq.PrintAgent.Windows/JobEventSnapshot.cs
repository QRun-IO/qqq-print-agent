/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

public sealed record JobEventSnapshot(string EventId, string Status, string RecordedAt, string? Message, string? SpoolerJobId, bool Delivered);
