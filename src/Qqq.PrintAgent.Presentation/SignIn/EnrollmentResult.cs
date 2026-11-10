/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>How an enrollment ended for the window: the service's confirmation to show as the status line when the device was enrolled, and whether the service could be reached at all (when it could not, the window shows its service-unavailable state).</summary>
public sealed record EnrollmentResult(string? StatusText, bool ServiceReachable);
