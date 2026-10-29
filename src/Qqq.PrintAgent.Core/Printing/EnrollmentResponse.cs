/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

public sealed record EnrollmentResponse(int ProtocolVersion, string DeviceId, string DeviceToken, int PollIntervalSeconds);


