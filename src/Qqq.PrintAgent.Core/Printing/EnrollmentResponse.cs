/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>The server's enrollment result: protocol version, the new device identity and its secret token, and the poll interval. The token is shown once, so it must be stored immediately and never logged.</summary>
public sealed record EnrollmentResponse(int ProtocolVersion, string DeviceId, string DeviceToken, int PollIntervalSeconds);


