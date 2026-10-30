/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.AccessControl;
using Qqq.PrintAgent.Windows;


namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentBrandingInstallerTests
{
   [Fact]
   public void StartMenuDeleteOnlyRulesAreAllowedWithoutRelaxingUpdaterProtection()
   {
      DirectorySecurity security = new();
      security.SetSecurityDescriptorSddlForm("O:SYG:SYD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)(A;OICI;0x10040;;;S-1-5-21-100-200-300-1001)");
      AgentBrandingInstaller.ValidateShortcutSecurity(security);
      Assert.Throws<UnauthorizedAccessException>(() => MsiUpdateInstaller.ValidateTrustedAcl(security, "S-1-5-18"));
   }

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

   [Fact]
   public void StartMenuCannotPassUntrustedWritePermissionsToItsShortcutFiles()
   {
      DirectorySecurity security = new();
      security.SetSecurityDescriptorSddlForm("O:SYG:SYD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OIIO;GW;;;S-1-5-21-100-200-300-1001)");
      Assert.Throws<UnauthorizedAccessException>(() => AgentBrandingInstaller.ValidateShortcutSecurity(security));
   }

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

   [Theory]
   [InlineData("..\\outside.lnk")]
   [InlineData("C:\\outside.lnk")]
   [InlineData("other.txt")]
   [InlineData("name.lnk:stream")]
   public void RegistryFilenameCannotEscapeItsOwnedDirectory(string filename)
   {
      Assert.Throws<InvalidDataException>(() => AgentBrandingInstaller.ShortcutPath(Path.GetTempPath(), filename));
   }

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
