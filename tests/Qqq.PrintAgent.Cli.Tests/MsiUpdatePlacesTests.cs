/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using KofTwentyTwo.AppKit.Logging;
using Qqq.PrintAgent.Core;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Where the installed product's update works. The factory decides which folders are trusted and which checks guard
/// them, so it is pinned here: nothing is created, read or checked on this computer, and neither the log nor the pipe
/// is opened; the factory only names two folders and binds four things, and none of them is called.
/// </summary>
public sealed class MsiUpdatePlacesTests
{
   /// <summary>
   /// The installed product updates under this computer's Program Files and its machine state folder, which is the
   /// folder the guard protects, with the guard's own check, the machine activity log, the installed service's pipe and
   /// the system clock.
   /// </summary>
   [Fact]
   public void InstalledProductIsBoundToProgramFilesTheGuardedStateFolderTheLogThePipeAndTheClock()
   {
      var places = MsiUpdatePlaces.Installed();
      Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), places.ProgramFiles);
      Assert.Equal(MachineStateDirectory.Root, places.StateDirectory);
      Assert.Equal<Action<string>>(MachineStateDirectory.EnsureForPath, places.EnsureProtectedState);
      Assert.Equal<Func<string, IActivityLog>>(AgentActivityLog.Create, places.OpenLog);
      Assert.Equal<Func<PipeRequest, Task<string>>>(AgentPipeClient.SendAsync, places.Send);
      Assert.Same(TimeProvider.System, places.Clock);
   }



   /// <summary>Every path the update trusts is named from the two folders given: the three installed programs' places under Program Files and the updates folder under the machine state.</summary>
   [Fact]
   public void EveryTrustedPathIsNamedFromTheTwoFoldersGiven()
   {
      var places = new MsiUpdatePlaces(@"X:\Programs", @"Y:\State", _ => { }, _ => new FakeActivityLog(), _ => Task.FromResult(""), TimeProvider.System);
      Assert.Equal(@"X:\Programs\QQQ Print Agent", places.InstalledRoot);
      Assert.Equal(@"X:\Programs\QQQ Print Agent\App\Qqq.PrintAgent.exe", places.InstalledCompanion);
      Assert.Equal(@"X:\Programs\QQQ Print Agent\Service\Qqq.PrintAgent.Service.exe", places.InstalledService);
      Assert.Equal(@"X:\Programs\QQQ Print Agent\Cli", places.InstalledCli);
      Assert.Equal(@"Y:\State\updates", places.UpdatesDirectory);
   }
}
