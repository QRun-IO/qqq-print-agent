/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

public sealed record DeviceControlCommand(string CommandId, string RequestId, string Action, string CreatedAt, string ExpiresAt, string? JobId = null);
