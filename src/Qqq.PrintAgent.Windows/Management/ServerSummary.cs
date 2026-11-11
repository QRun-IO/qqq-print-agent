/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>What is needed to name, order and select a server: its identifier, name, address and position, its health with the text that explains it, and whether it is being removed.</summary>
public sealed record ServerSummary(string ServerId, string Name, string Address, int Position, string Health, string Status, bool Removing = false);
