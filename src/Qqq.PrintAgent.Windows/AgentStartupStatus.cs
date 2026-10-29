/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>Current user's explicitly configured companion sign-in task.</summary>
public sealed record AgentStartupStatus(bool Enabled, bool Available, string Guidance);
