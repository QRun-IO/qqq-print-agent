/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Where a file is staged for a privileged Windows component to read, and the two checks that make that safe: one that the
/// folder is the protected machine state (it throws when the folder cannot be trusted), and one that a file just written
/// there has a trusted owner and access list (it throws otherwise). The installed product takes <see cref="MachineSetup"/>;
/// a test passes a temporary folder and checks of its own, because a temporary file can never pass the real ones.
/// </summary>
internal sealed record ProtectedStaging(string Directory, Action<string> EnsureFolder, Action<string> ValidateFile)
{
   /// <summary>
   /// The staging of the installed product, so the entry point cannot bind a weaker check by accident: the setup folder
   /// inside the machine state folder, with the machine state guard's own folder and file checks.
   /// </summary>
   internal static ProtectedStaging MachineSetup() => new(Path.Combine(AgentStore.DefaultDirectory, "setup"), MachineStateDirectory.EnsureForPath, MachineStateDirectory.ValidateFile);
}
