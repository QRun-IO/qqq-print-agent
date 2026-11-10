/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Serialization;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Most recent local observation and bounded operator metadata; no document bytes. <see cref="EarlierPairing"/> marks a job
/// recorded under a pairing this server has since replaced: it is history, shown but not acknowledged, retried or
/// cancelled. It is written only when true, so a job that is not from an earlier pairing is the same JSON as before the
/// member existed, and what version 1 of the pipe sends is unchanged. The two times are carried as the text the journal
/// holds, ISO 8601 as the service wrote it; they are not converted on the way to a client.
/// </summary>
public sealed record JobSnapshot(string JobId, string Status, string UpdatedAt, string? Message, string? SpoolerJobId,
   string? PrinterId = null, string? PrinterName = null, string? Title = null, string? Format = null, bool FailureAcknowledged = false, string? CreatedAt = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool EarlierPairing = false);
