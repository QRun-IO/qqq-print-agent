/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Service;

/// <summary>The service's confirmation that it took over a session: its sentence for the operator, and what still has to happen before printing works.</summary>
public sealed record SignInConfirmation(string Message, SignInReadiness Readiness);
