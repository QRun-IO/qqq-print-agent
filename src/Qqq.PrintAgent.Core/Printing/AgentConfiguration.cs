/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

public sealed record AgentConfiguration(string ServerUrl, string DeviceId, string DeviceToken, int PollIntervalSeconds = 5);


