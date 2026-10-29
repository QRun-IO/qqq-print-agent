/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

public sealed record PrintEvent(string EventId, string Status, string? Message = null, string? SpoolerJobId = null);


