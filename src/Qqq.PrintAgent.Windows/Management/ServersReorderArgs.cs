/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>Arguments of <see cref="ManagementOperations.ServersReorder"/>: every server's identifier, each once, in the new order. A list with a null in it is refused.</summary>
public sealed record ServersReorderArgs(IReadOnlyList<string> ServerIds);
