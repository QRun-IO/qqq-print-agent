/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>What this computer sends to pair itself: the one-time code, a display name and the agent version, so the server can check compatibility before issuing a credential.</summary>
public sealed record EnrollmentRequest(string EnrollmentCode, string DeviceName, string AgentVersion);


