/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>One bounded, credential-free local runtime observation.</summary>
public sealed record AgentLogEntry(string Timestamp, string Level, string Message);

