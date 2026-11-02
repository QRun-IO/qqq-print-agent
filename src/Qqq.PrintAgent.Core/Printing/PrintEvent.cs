/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>One status step reported to the server for a job, with a unique event id so a retried report is not counted twice, and the spooler job id once one exists.</summary>
public sealed record PrintEvent(string EventId, string Status, string? Message = null, string? SpoolerJobId = null);


