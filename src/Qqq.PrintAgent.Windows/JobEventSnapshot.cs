/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>One saved status step of a job as shown in its history, including whether the server has acknowledged it, so the operator can tell reported progress from progress still waiting to be sent. The time is carried as the text the journal holds, ISO 8601 as the service wrote it; it is not converted on the way to a client.</summary>
public sealed record JobEventSnapshot(string EventId, string Status, string RecordedAt, string? Message, string? SpoolerJobId, bool Delivered);
