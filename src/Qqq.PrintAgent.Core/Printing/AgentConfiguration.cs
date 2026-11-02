/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>The saved server address, device identity and poll interval for one enrollment; the device token is the secret, so this is stored only in protected machine state and never logged.</summary>
public sealed record AgentConfiguration(string ServerUrl, string DeviceId, string DeviceToken, int PollIntervalSeconds = 5);


