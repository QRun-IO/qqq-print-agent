/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// What the installed product stages the sign-in task definition in. The factory is the only place the real folder and
/// the machine state guard's checks are chosen, so this pins that both checks are the guard's own and that the folder
/// is inside the one the guard protects. Nothing is created, read or checked on this computer: the factory only names
/// a place and two checks, and no check is called.
/// </summary>
public sealed class ProtectedStagingTests
{
   /// <summary>
   /// The definition is staged in the setup folder inside the machine state folder, which is the folder the guard
   /// protects, and both checks are the guard's own: the folder check and the check of the staged file.
   /// </summary>
   [Fact]
   public void InstalledProductStagesInTheMachineStateFolderWithTheGuardsChecks()
   {
      var staging = ProtectedStaging.MachineSetup();
      Assert.Equal(Path.Combine(MachineStateDirectory.Root, "setup"), staging.Directory);
      Assert.Equal<Action<string>>(MachineStateDirectory.EnsureForPath, staging.EnsureFolder);
      Assert.Equal<Action<string>>(MachineStateDirectory.ValidateFile, staging.ValidateFile);
   }
}
