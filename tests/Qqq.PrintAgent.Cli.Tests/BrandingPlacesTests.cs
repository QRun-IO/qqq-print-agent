/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// What the installed product binds its branding rules to. The factory is the only place the real folders and the
/// machine state guard's check are chosen, so this pins that the check is the guard's own and that the state folder is
/// the one the guard protects. Nothing is created, read or checked on this computer: the factory only names places and
/// checks, and no check is called.
/// </summary>
public sealed class BrandingPlacesTests
{
   /// <summary>
   /// Branding is bound to the companion under Program Files, the all-users Start menu, the machine state folder with
   /// the guard's own check of it, and the machine's branding; each note goes to the given log marked as the installer's.
   /// </summary>
   [Fact]
   public void InstalledProductIsBoundToTheRealPlacesAndTheMachineStateGuard()
   {
      List<string> log = [];
      var places = BrandingPlaces.Installed(log.Add);
      Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "QQQ Print Agent", "App", "Qqq.PrintAgent.exe"), places.InstalledExecutable);
      Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), places.StartMenuPrograms);
      Assert.Equal(MachineStateDirectory.Root, places.StateDirectory);
      Assert.Equal<Action<string>>(MachineStateDirectory.EnsureForPath, places.EnsureProtectedState);
      Assert.Equal<Func<bool, RuntimeBranding>>(RuntimeBranding.LoadMachine, places.LoadBranding);
      Assert.Empty(log);
      places.Note("Something was left alone.");
      Assert.Equal(new[] { "[Installer] Something was left alone." }, log, StringComparer.Ordinal);
   }
}
