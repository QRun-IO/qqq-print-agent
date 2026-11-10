/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>Arguments of <see cref="ManagementOperations.ServersRename"/>: the server's new name. Its address cannot be changed; a different address is a different server.</summary>
public sealed record ServersRenameArgs(string Name);
