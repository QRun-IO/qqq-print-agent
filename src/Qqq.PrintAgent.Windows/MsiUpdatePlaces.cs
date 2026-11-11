/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using KofTwentyTwo.AppKit.Logging;
using Qqq.PrintAgent.Core;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Where a machine update works and what it reaches outside itself: the Program Files folder the product is installed
/// under, the machine state folder with the check that it is protected (it throws when the folder cannot be trusted),
/// how the activity log of a state folder is opened, how a request reaches the service, and the clock. The installed
/// product takes its places from <see cref="Installed"/>; a test passes temporary folders, a pipe of its own and a
/// clock it moves itself.
/// </summary>
internal sealed record MsiUpdatePlaces(string ProgramFiles, string StateDirectory, Action<string> EnsureProtectedState, Func<string, IActivityLog> OpenLog, Func<PipeRequest, Task<string>> Send, TimeProvider Clock)
{
   /// <summary>
   /// The places of the installed product, so the entry point cannot bind a weaker check by accident: this computer's
   /// Program Files, the machine state folder checked by the machine state guard, the machine activity log, the
   /// installed service's pipe and the system clock.
   /// </summary>
   internal static MsiUpdatePlaces Installed()
      => new(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AgentStore.DefaultDirectory, MachineStateDirectory.EnsureForPath, AgentActivityLog.Create, AgentPipeClient.SendAsync, TimeProvider.System);



   /// <summary>The fixed machine-wide install folder; update trust decisions compare against it, never against a path supplied by the caller.</summary>
   internal string InstalledRoot => Path.Combine(ProgramFiles, "QQQ Print Agent");



   /// <summary>The companion executable at its install path; only that process may ask for an update to be prepared.</summary>
   internal string InstalledCompanion => Path.Combine(InstalledRoot, "App", "Qqq.PrintAgent.exe");



   /// <summary>The service executable at its install path; its version is checked after an install.</summary>
   internal string InstalledService => Path.Combine(InstalledRoot, "Service", "Qqq.PrintAgent.Service.exe");



   /// <summary>The installed command-line folder that is copied to run the update, so replacing the installed binaries does not lock the running helper.</summary>
   internal string InstalledCli => Path.Combine(InstalledRoot, "Cli");



   /// <summary>The protected machine folder where downloaded installers, helper copies and update requests are staged; only administrators can write there.</summary>
   internal string UpdatesDirectory => Path.Combine(StateDirectory, "updates");
}
