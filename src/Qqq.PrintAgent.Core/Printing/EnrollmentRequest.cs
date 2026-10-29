/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

public sealed record EnrollmentRequest(string EnrollmentCode, string DeviceName, string AgentVersion);


