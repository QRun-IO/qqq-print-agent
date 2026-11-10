/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>
/// How an enrollment ended for the window: the service's confirmation to show as the status line when the device was
/// enrolled, whether the service could be reached at all (when it could not, the window shows its service-unavailable
/// state), and whether the enrollment was started. It is not started while another operation is running; the window then
/// keeps the code the operator typed, because it was not used.
/// </summary>
public sealed record EnrollmentResult(string? StatusText, bool ServiceReachable, bool Started = true);
