/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.Principal;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Which folder an update may be staged in. Everything staged there later runs with administrator rights, so the folder
/// must be asked for by an administrator, lie inside the updates folder of trusted machine state, be reached through no
/// link, and be owned and writable only by trusted accounts at every level up to the machine state folder. The checks
/// run in temporary folders with a fake of the folder security; the links are real junctions.
/// </summary>
public sealed class MsiUpdateStagingTests
{
   /// <summary>What someone who is not an administrator is told.</summary>
   private const string NotAdministrator = "Machine updates require an elevated administrator. No installer was started.";



   /// <summary>What a path outside the updates folder is refused with.</summary>
   private const string Outside = "Update paths must remain inside the protected staging directory.";



   /// <summary>
   /// A session folder inside trusted machine state is accepted, whether it exists yet or not, and so is the updates
   /// folder itself. The machine state check is asked about the very path, and the owner and access list of every
   /// existing folder from the session up to the machine state folder are read, and of nothing above it.
   /// </summary>
   [Fact]
   public void SessionInsideTrustedMachineStateIsAccepted()
   {
      using var setup = new MsiUpdateSetup();
      setup.Steps.ValidateStagingDirectory(setup.Session);
      Assert.Equal(new[] { setup.Session }, setup.Guarded, StringComparer.Ordinal);
      Assert.Equal(new[] { setup.Session, setup.Places.UpdatesDirectory, setup.State }, setup.Security.Reads, StringComparer.Ordinal);
      setup.Security.Reads.Clear();
      setup.Steps.ValidateStagingDirectory(Path.Combine(setup.Places.UpdatesDirectory, "not-yet-created"));
      Assert.Equal(new[] { setup.Places.UpdatesDirectory, setup.State }, setup.Security.Reads, StringComparer.Ordinal);
      setup.Steps.ValidateStagingDirectory(setup.Places.UpdatesDirectory);
      Assert.Equal(Path.Combine(setup.State, "updates"), setup.Steps.UpdatesDirectory);
   }



   /// <summary>Someone who is not an administrator is refused before the machine state is checked or any owner is read.</summary>
   [Fact]
   public void NonAdministratorIsRefusedBeforeAnythingIsChecked()
   {
      using var setup = new MsiUpdateSetup();
      setup.Security.IsAdministrator = false;
      UnauthorizedAccessException refusal = Assert.Throws<UnauthorizedAccessException>(() => setup.Steps.ValidateStagingDirectory(setup.Session));
      Assert.Equal(NotAdministrator, refusal.Message);
      Assert.Empty(setup.Guarded);
      Assert.Empty(setup.Security.Reads);
   }



   /// <summary>Machine state that cannot be trusted stops the check with the guard's own refusal, before any owner is read.</summary>
   [Fact]
   public void UntrustedMachineStateStopsTheCheck()
   {
      using var setup = new MsiUpdateSetup();
      setup.GuardFailure = new UnauthorizedAccessException("Synthetic: the machine print data directory is not trusted.");
      Assert.Same(setup.GuardFailure, Assert.Throws<UnauthorizedAccessException>(() => setup.Steps.ValidateStagingDirectory(setup.Session)));
      Assert.Empty(setup.Security.Reads);
   }



   /// <summary>
   /// A folder outside the updates folder is refused: the machine state folder itself, a folder beside the updates
   /// folder whose name only begins the same way, and a path that climbs out of it with "..".
   /// </summary>
   [Theory]
   [InlineData("")]
   [InlineData("updates-other/session")]
   [InlineData("updates/../logs")]
   [InlineData("updates/session/../../../elsewhere")]
   public void FolderOutsideTheUpdatesFolderIsRefused(string relative)
   {
      using var setup = new MsiUpdateSetup();
      InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => setup.Steps.ValidateStagingDirectory(Path.Combine(setup.State, relative)));
      Assert.Equal(Outside, refusal.Message);
      Assert.Empty(setup.Security.Reads);
   }



   /// <summary>Without a machine state folder there is no installation to update, and that is what is said.</summary>
   [Fact]
   public void MissingMachineStateFolderMeansNoInstallation()
   {
      using var setup = new MsiUpdateSetup();
      Directory.Delete(setup.State, recursive: true);
      InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => setup.Steps.ValidateStagingDirectory(setup.Session));
      Assert.Equal("Install the machine MSI before staging updates.", refusal.Message);
   }



   /// <summary>A session folder that is really a junction to somewhere else is refused, and so is a folder reached through one, before any owner is read.</summary>
   [Theory]
   [InlineData("")]
   [InlineData("inside")]
   public void FolderReachedThroughAJunctionIsRefused(string below)
   {
      using var setup = new MsiUpdateSetup();
      string elsewhere = Path.Combine(setup.Root, "elsewhere");
      Directory.CreateDirectory(Path.Combine(elsewhere, "inside"));
      string junction = Path.Combine(setup.Places.UpdatesDirectory, "linked");
      MsiUpdateSetup.MakeJunction(junction, elsewhere);
      IOException refusal = Assert.Throws<IOException>(() => setup.Steps.ValidateStagingDirectory(Path.Combine(junction, below)));
      Assert.Equal("Update paths cannot contain symbolic links or junctions.", refusal.Message);
      Assert.Empty(setup.Security.Reads);
   }



   /// <summary>A file where a folder belongs, at the session or above it, is refused.</summary>
   [Theory]
   [InlineData("occupied")]
   [InlineData("occupied/session")]
   public void FileWhereAFolderBelongsIsRefused(string relative)
   {
      using var setup = new MsiUpdateSetup();
      File.WriteAllText(Path.Combine(setup.Places.UpdatesDirectory, "occupied"), "synthetic");
      IOException refusal = Assert.Throws<IOException>(() => setup.Steps.ValidateStagingDirectory(Path.Combine(setup.Places.UpdatesDirectory, relative)));
      Assert.Equal("An update directory path points to a file.", refusal.Message);
   }



   /// <summary>A folder ordinary users may write to is refused at every level: the session, the updates folder and the machine state folder.</summary>
   [Theory]
   [InlineData(0)]
   [InlineData(1)]
   [InlineData(2)]
   public void FolderOrdinaryUsersMayWriteToIsRefusedAtEveryLevel(int level)
   {
      using var setup = new MsiUpdateSetup();
      string folder = new[] { setup.Session, setup.Places.UpdatesDirectory, setup.State }[level];
      setup.Security.Descriptors[folder] = MsiUpdateSetup.WritableByUsers();
      UnauthorizedAccessException refusal = Assert.Throws<UnauthorizedAccessException>(() => setup.Steps.ValidateStagingDirectory(setup.Session));
      Assert.Equal("An update path allows modification by an untrusted identity. Repair the MSI directory permissions.", refusal.Message);
   }



   /// <summary>
   /// A folder the administrator who is asking owns is trusted, because that administrator can change it anyway; a
   /// folder some other user owns is not.
   /// </summary>
   [Fact]
   public void FolderOwnedByTheAskingAdministratorIsTrustedAndAnotherUsersIsNot()
   {
      using var setup = new MsiUpdateSetup();
      setup.Security.Descriptors[setup.Session] = FakePathSecurity.Descriptor(owner: setup.Security.CurrentUserSid!);
      setup.Steps.ValidateStagingDirectory(setup.Session);
      setup.Security.Descriptors[setup.Session] = FakePathSecurity.Descriptor(owner: "S-1-5-21-100-200-300-2002");
      UnauthorizedAccessException refusal = Assert.Throws<UnauthorizedAccessException>(() => setup.Steps.ValidateStagingDirectory(setup.Session));
      Assert.Equal("An update path is not owned by a trusted administrator or Windows identity.", refusal.Message);
   }



   /// <summary>
   /// The installed product's steps are bound to this computer. They work in exactly the places of the installed
   /// product, with the machine state guard, the machine log and the installed service's pipe among them; the binding
   /// is only compared, and none of those is called. They stage under the machine state folder, run as the process
   /// that calls them, never take a development or test copy for the installed companion, and ask the real process
   /// token who is asking. The last is shown with a folder outside the machine state, which nothing is ever read from
   /// or created in: without administrator rights the request is refused for that reason, and with them for being
   /// outside the updates folder.
   /// </summary>
   [Fact]
   public void InstalledProductsStepsAreBoundToThisComputer()
   {
      Assert.Equal(MsiUpdatePlaces.Installed(), MsiUpdateInstaller.Installed.Places);
      Assert.Equal(Path.Combine(Qqq.PrintAgent.Core.MachineStateDirectory.Root, "updates"), MsiUpdateInstaller.Installed.UpdatesDirectory);
      Assert.Equal(Environment.ProcessId, MsiUpdateInstaller.Installed.CurrentProcessId);
      Assert.False(MsiUpdateInstaller.Installed.RunsAsInstalledCompanion);
      using var identity = WindowsIdentity.GetCurrent();
      bool administrator = identity.Groups!.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
      string outside = Path.Combine(Path.GetTempPath(), "qqq-no-such-update-session-" + Guid.NewGuid().ToString("N"));
      Exception refusal = Assert.ThrowsAny<Exception>(() => MsiUpdateInstaller.Installed.ValidateStagingDirectory(outside));
      Assert.Equal(administrator ? typeof(InvalidDataException) : typeof(UnauthorizedAccessException), refusal.GetType());
      Assert.Equal(administrator ? Outside : NotAdministrator, refusal.Message);
      Assert.False(Directory.Exists(outside));
   }
}
