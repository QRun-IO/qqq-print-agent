/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>The result of asking for work: a job claimed for this device, or none. A claim is exclusive, so only this agent may print that job.</summary>
public sealed record JobClaim(PrintJob? Job);


