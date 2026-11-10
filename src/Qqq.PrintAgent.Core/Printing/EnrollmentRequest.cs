/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>What this computer sends to pair itself: the one-time code, a display name and the agent version, so the server can check compatibility before issuing a credential.</summary>
public sealed record EnrollmentRequest(string EnrollmentCode, string DeviceName, string AgentVersion)
{
   /// <summary>Returns a fixed redacted label, so logging or displaying the request can never reveal the enrollment code.</summary>
   public override string ToString() => "Enrollment request (redacted)";
}


