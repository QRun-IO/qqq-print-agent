/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>One operator command from the server (for example pause or cancel a job). The ids make it idempotent and the expiry makes a stale command ignorable, so a late delivery cannot act on a situation that has changed.</summary>
public sealed record DeviceControlCommand(string CommandId, string RequestId, string Action, string CreatedAt, string ExpiresAt, string? JobId = null);
