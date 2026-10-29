/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>Most recent local observation for one job; no document bytes or customer title.</summary>
public sealed record JobSnapshot(string JobId, string Status, string UpdatedAt, string? Message, string? SpoolerJobId);

