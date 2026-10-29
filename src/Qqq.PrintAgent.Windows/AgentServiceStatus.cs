/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>Local SCM state, independently of cloud connectivity or the management pipe.</summary>
public sealed record AgentServiceStatus(bool Installed, string State, bool CanStart, bool CanRestart, string Guidance);
