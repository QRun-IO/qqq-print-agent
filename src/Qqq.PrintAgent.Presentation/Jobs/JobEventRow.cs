/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Jobs;

/// <summary>One row of a job's event table: when it was recorded, the status in words, and a message that also says whether the event was reported to the server and which Windows job it belongs to.</summary>
public sealed record JobEventRow(string Time, string Status, string Message);
