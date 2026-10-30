/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>Most recent local observation and bounded operator metadata; no document bytes.</summary>
public sealed record JobSnapshot(string JobId, string Status, string UpdatedAt, string? Message, string? SpoolerJobId,
   string? PrinterId = null, string? PrinterName = null, string? Title = null, string? Format = null, bool FailureAcknowledged = false, string? CreatedAt = null);

