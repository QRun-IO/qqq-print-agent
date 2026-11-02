/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.AccessControl;
using Qqq.PrintAgent.Windows;


namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The installer's branded Start Menu shortcut is created and removed by a privileged component, so the folder permissions it
/// trusts, the file names it generates and the shortcuts it is willing to delete are all validated here. The rules must let
/// ordinary users keep a delete-only grant without weakening the stricter protection the updater requires.
/// </summary>
public sealed class AgentBrandingInstallerTests
{
   /// <summary>A Start Menu folder that gives ordinary users read/execute, and one user delete-only rights, is acceptable for shortcuts, yet the same permissions still fail the stricter updater check, so relaxing one rule does not relax the other.</summary>
   [Fact]
   public void StartMenuDeleteOnlyRulesAreAllowedWithoutRelaxingUpdaterProtection()
   {
      DirectorySecurity security = new();
      security.SetSecurityDescriptorSddlForm("O:SYG:SYD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)(A;OICI;0x10040;;;S-1-5-21-100-200-300-1001)");
      AgentBrandingInstaller.ValidateShortcutSecurity(security);
      Assert.Throws<UnauthorizedAccessException>(() => MsiUpdateInstaller.ValidateTrustedAcl(security, "S-1-5-18"));
   }



   /// <summary>Rights that let an untrusted user add content, change permissions or take ownership of the Start Menu folder (write-data, append, write-DAC, write-owner and the generic write/all rights) are refused, because they could be used to plant or redirect a shortcut.</summary>
   [Theory]
   [InlineData("0x2")]
   [InlineData("0x4")]
   [InlineData("0x40000")]
   [InlineData("0x80000")]
   [InlineData("GW")]
   [InlineData("GA")]
   public void StartMenuUntrustedContentAndPermissionChangesRemainRejected(string rights)
   {
      DirectorySecurity security = new();
      security.SetSecurityDescriptorSddlForm("O:SYG:SYD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;" + rights + ";;;S-1-5-21-100-200-300-1001)");
      Assert.Throws<UnauthorizedAccessException>(() => AgentBrandingInstaller.ValidateShortcutSecurity(security));
   }



   /// <summary>An inherit-only write grant to an untrusted user is refused too: it grants nothing on the folder itself but would give that user write access to every shortcut file created inside it.</summary>
   [Fact]
   public void StartMenuCannotPassUntrustedWritePermissionsToItsShortcutFiles()
   {
      DirectorySecurity security = new();
      security.SetSecurityDescriptorSddlForm("O:SYG:SYD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OIIO;GW;;;S-1-5-21-100-200-300-1001)");
      Assert.Throws<UnauthorizedAccessException>(() => AgentBrandingInstaller.ValidateShortcutSecurity(security));
   }



   /// <summary>A normal customer label becomes its own .lnk name. An over-long label is shortened to at most 120 characters in a repeatable way, with a suffix that differs when only the last character differs, so two long names never collide.</summary>
   [Fact]
   public void FriendlyShortcutKeepsItsLabelAndLongNamesHaveStableDistinctSuffixes()
   {
      Assert.Equal("Acme Print Connector.lnk", AgentBrandingInstaller.ShortcutFileName("Acme Print Connector"));
      string longName = new('x', 180);
      string first = AgentBrandingInstaller.ShortcutFileName(longName);
      Assert.Equal(first, AgentBrandingInstaller.ShortcutFileName(longName));
      Assert.NotEqual(first, AgentBrandingInstaller.ShortcutFileName(longName[..179] + "y"), StringComparer.Ordinal);
      Assert.True(first.Length <= 120);
   }



   /// <summary>A customer-supplied label can contain path separators, drive letters, reserved device names or illegal characters; each is turned into a single safe .lnk file name that resolves directly inside the shortcut folder and nowhere else.</summary>
   [Theory]
   [InlineData("../outside")]
   [InlineData("C:\\outside")]
   [InlineData("CON")]
   [InlineData("NUL.txt")]
   [InlineData("Acme / East: Print*")]
   public void UnsafeLabelsBecomeSingleSafeFilenames(string label)
   {
      string filename = AgentBrandingInstaller.ShortcutFileName(label);
      string directory = Path.Combine(Path.GetTempPath(), "synthetic-shortcuts");
      string path = AgentBrandingInstaller.ShortcutPath(directory, filename);
      Assert.Equal(directory, Path.GetDirectoryName(path));
      Assert.Equal(filename, Path.GetFileName(path));
      Assert.EndsWith(".lnk", filename, StringComparison.Ordinal);
   }



   /// <summary>The shortcut file name read back from the registry is untrusted: a parent-directory path, a drive path, a non-.lnk extension or an alternate data stream is rejected, so removal can never be steered outside the folder the installer owns.</summary>
   [Theory]
   [InlineData("..\\outside.lnk")]
   [InlineData("C:\\outside.lnk")]
   [InlineData("other.txt")]
   [InlineData("name.lnk:stream")]
   public void RegistryFilenameCannotEscapeItsOwnedDirectory(string filename)
   {
      Assert.Throws<InvalidDataException>(() => AgentBrandingInstaller.ShortcutPath(Path.GetTempPath(), filename));
   }



   /// <summary>Uninstall deletes a shortcut only if it points at the installed companion, has no extra arguments and carries the product's ownership description; a user's own shortcut with the same name, or one aimed elsewhere, is left alone.</summary>
   [Fact]
   public void CleanupRequiresExactTargetArgumentsAndOwnershipMarker()
   {
      string target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "QQQ Print Agent", "App", "Qqq.PrintAgent.exe");
      Assert.True(AgentBrandingInstaller.MatchesOwnedShortcut(target, "", "QQQ Print Agent companion"));
      Assert.False(AgentBrandingInstaller.MatchesOwnedShortcut(target, "--other", "QQQ Print Agent companion"));
      Assert.False(AgentBrandingInstaller.MatchesOwnedShortcut(target, "", "Someone else's shortcut"));
      Assert.False(AgentBrandingInstaller.MatchesOwnedShortcut(Path.Combine(Path.GetTempPath(), "Qqq.PrintAgent.exe"), "", "QQQ Print Agent companion"));
   }
}
