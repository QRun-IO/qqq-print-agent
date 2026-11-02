/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.AccessControl;
using System.Security.Cryptography;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The update installer runs as the privileged service and executes a downloaded MSI, so every input it trusts (paths, request
/// fields, folder permissions, package bytes, installed versions) is checked here at its boundary.
/// </summary>
public sealed class MsiUpdateInstallerTests
{
   /// <summary>
   /// A path escaping the update folder with .. or sharing only its name prefix (updates-other next to updates) is refused, a
   /// real child is accepted, and the folder itself does not count as a file inside itself.
   /// </summary>
   [Theory]
   [InlineData("../escape/file.msi")]
   [InlineData("../updates-other/file.msi")]
   public void ContainmentRejectsEscapesAndSiblingPrefix(string relative)
   {
      string root = Path.Combine(Path.GetTempPath(), "updates");
      Assert.Throws<InvalidDataException>(() => MsiUpdateInstaller.ValidateContainedPath(Path.Combine(root, relative), root, allowRoot: false));
      MsiUpdateInstaller.ValidateContainedPath(Path.Combine(root, "session", "package.msi"), root, allowRoot: false);
      Assert.Throws<InvalidDataException>(() => MsiUpdateInstaller.ValidateContainedPath(root, root, allowRoot: false));
   }



   /// <summary>
   /// An install request is honored only for an eligible target version (0.1.2-rc.1 is refused, 0.1.3-rc.1 accepted), an .msi
   /// file inside the staging directory, a well-formed 64-character hash and a real parent process id. The last two are also
   /// broken independently of the first two, so a valid request cannot mask a missing hash or process check.
   /// </summary>
   [Theory]
   [InlineData("0.1.2-rc.1", "package.msi", false)]
   [InlineData("0.1.3-rc.1", "package.msi", true)]
   [InlineData("0.1.3", "package.exe", false)]
   [InlineData("0.1.3", "../other/package.msi", false)]
   public void RequestRequiresEligibleVersionAndSameDirectoryPackage(string version, string filename, bool valid)
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-update-request");
      var request = new MsiUpdateInstaller.InstallRequest(Path.Combine(directory, filename), version, new string('A', 64), 123, 456);
      if(valid)
      {
         MsiUpdateInstaller.ValidateRequest(request, directory);
      }
      else
      {
         Assert.Throws<InvalidDataException>(() => MsiUpdateInstaller.ValidateRequest(request, directory));
      }
      Assert.Throws<InvalidDataException>(() => MsiUpdateInstaller.ValidateRequest(request with { Sha256 = "invalid" }, directory));
      Assert.Throws<InvalidDataException>(() => MsiUpdateInstaller.ValidateRequest(request with { ParentProcessId = 0 }, directory));
   }



   /// <summary>
   /// The staging folder must be owned by Administrators and writable only by SYSTEM and Administrators; read access or an
   /// inherit-only creator-owner grant is harmless, but write or full-control for ordinary users, another owner, or no access
   /// list at all is refused. A user who could write there could swap the package after its hash was checked.
   /// </summary>
   [Theory]
   [InlineData("O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)", true)]
   [InlineData("O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;BU)", true)]
   [InlineData("O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;OICIIO;GA;;;CO)", true)]
   [InlineData("O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FW;;;BU)", false)]
   [InlineData("O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GA;;;BU)", false)]
   [InlineData("O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GW;;;BU)", false)]
   [InlineData("O:BUD:P(A;;FA;;;SY)(A;;FA;;;BA)", false)]
   [InlineData("O:BAD:NO_ACCESS_CONTROL", false)]
   public void ProtectedAclRejectsUntrustedWritersAndOwners(string sddl, bool valid)
   {
      DirectorySecurity security = new();
      security.SetSecurityDescriptorSddlForm(sddl);
      if(valid)
      {
         MsiUpdateInstaller.ValidateTrustedAcl(security, administratorSid: null);
      }
      else
      {
         Assert.Throws<UnauthorizedAccessException>(() => MsiUpdateInstaller.ValidateTrustedAcl(security, administratorSid: null));
      }
   }



   /// <summary>
   /// Opening the package verifies its hash and then holds the file open so it cannot be rewritten while the installer runs. A
   /// changed or emptied file is rejected, which closes the gap between checking the bytes and executing them.
   /// </summary>
   [Fact]
   public void ApplyHashValidationDetectsMutationAndLocksVerifiedBytes()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-update-hash-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(directory);
      string file = Path.Combine(directory, "test.msi");
      byte[] bytes = [1, 2, 3, 4];
      string hash = Convert.ToHexString(SHA256.HashData(bytes));
      try
      {
         File.WriteAllBytes(file, bytes);
         using(FileStream locked = MsiUpdateInstaller.OpenVerifiedPackage(file, hash))
         {
            Assert.Equal(bytes.Length, locked.Length);
            Assert.Throws<IOException>(() => File.WriteAllBytes(file, [5, 6]));
         }
         File.WriteAllBytes(file, [5, 6]);
         Assert.Throws<InvalidDataException>(() => MsiUpdateInstaller.OpenVerifiedPackage(file, hash));
         File.WriteAllBytes(file, []);
         Assert.Throws<InvalidDataException>(() => MsiUpdateInstaller.OpenVerifiedPackage(file, hash));
      }
      finally { Directory.Delete(directory, recursive: true); }
   }



   /// <summary>
   /// msiexec is started directly with an argument list (no shell, so spaces in paths cannot inject anything), in passive mode
   /// with no automatic restart and a verbose log. Passive keeps the progress visible to the signed-in user instead of silent.
   /// </summary>
   [Fact]
   public void InstallerArgumentsAreStructuredAndNeverRequestSilentRebootOrShell()
   {
      System.Diagnostics.ProcessStartInfo start = MsiUpdateInstaller.CreateInstallerStart(@"C:\protected update\package.msi", @"C:\protected update\install.log");
      Assert.False(start.UseShellExecute);
      Assert.True(start.CreateNoWindow);
      Assert.EndsWith("msiexec.exe", start.FileName, StringComparison.OrdinalIgnoreCase);
      Assert.Equal(new[] { "/i", @"C:\protected update\package.msi", "/passive", "/norestart", "/L*v", @"C:\protected update\install.log" }, start.ArgumentList, StringComparer.Ordinal);
   }



   /// <summary>Windows Installer exit codes are translated into distinct operator messages: success, restart needed, user cancel, and a generic fatal error that claims no completed update.</summary>
   [Theory]
   [InlineData(0, "successfully")]
   [InlineData(3010, "requires a restart")]
   [InlineData(1602, "canceled")]
   [InlineData(1603, "did not report a completed update")]
   public void InstallerOutcomesDistinguishFailureCancellationAndRequiredReboot(int code, string expected)
   {
      Assert.Contains(expected, MsiUpdateInstaller.DescribeExitCode(code), StringComparison.Ordinal);
   }



   /// <summary>
   /// Success is decided by the version actually installed, not by the installer's exit code. Build metadata after + is ignored,
   /// but a different core version, an older one or an unreadable one all fail.
   /// </summary>
   [Theory]
   [InlineData("0.1.3-rc.1+abc123", "0.1.3-rc.1", true)]
   [InlineData("0.1.3-rc.1", "0.1.3", false)]
   [InlineData("0.1.2", "0.1.3-rc.1", false)]
   [InlineData(null, "0.1.3-rc.1", false)]
   public void ActualInstalledVersionMustMatchBeforeSuccess(string? installed, string requested, bool expected)
   {
      Assert.Equal(expected, MsiUpdateInstaller.InstalledVersionMatches(installed, requested));
   }



   /// <summary>
   /// After the installer reports success, a companion, service or CLI that is still on the old version or has no readable
   /// version means a partial install. The error must name the component and say the installer returned success without claiming
   /// a rollback happened, so the operator is not told the machine was restored when it was not.
   /// </summary>
   [Theory]
   [InlineData("Qqq.PrintAgent.exe", "0.3.1-rc.1")]
   [InlineData("Qqq.PrintAgent.Service.exe", "0.3.1-rc.1")]
   [InlineData("qqq-print-agent.exe", "0.3.1-rc.1")]
   [InlineData("Qqq.PrintAgent.Service.exe", null)]
   [InlineData("qqq-print-agent.exe", null)]
   public void SuccessfulMsiDoesNotEstablishCoherenceWhenAnyComponentIsOldOrMissing(string component, string? version)
   {
      const string requested = "0.3.2-rc.1";
      InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
         MsiUpdateInstaller.ValidateInstalledVersions(requested, path =>
            string.Equals(Path.GetFileName(path), component, StringComparison.Ordinal) ? version : requested + "+reviewed-source"));
      Assert.Contains("Windows Installer returned success", failure.Message, StringComparison.Ordinal);
      Assert.Contains(component, failure.Message, StringComparison.Ordinal);
      Assert.DoesNotContain("rolled back", failure.Message, StringComparison.OrdinalIgnoreCase);
   }



   /// <summary>An executable that cannot be read after installation is reported as an unverified version with the original file error preserved, not as a generic failure.</summary>
   [Fact]
   public void MissingInstalledExecutableReportsUnverifiedVersionAfterMsiSuccess()
   {
      InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
         MsiUpdateInstaller.ValidateInstalledVersions("0.3.2-rc.1", path =>
            string.Equals(Path.GetFileName(path), "Qqq.PrintAgent.Service.exe", StringComparison.Ordinal)
               ? throw new FileNotFoundException("Synthetic missing service executable.")
               : "0.3.2-rc.1"));
      Assert.IsType<FileNotFoundException>(failure.InnerException);
      Assert.Contains("Windows Installer returned success", failure.Message, StringComparison.Ordinal);
      Assert.Contains("Qqq.PrintAgent.Service.exe version could not be verified", failure.Message, StringComparison.Ordinal);
   }



   /// <summary>
   /// All three installed programs must carry identical build metadata. A difference between any two, or one program having
   /// metadata and another none, shows mixed builds on one machine and is reported as incoherent.
   /// </summary>
   [Theory]
   [InlineData("+source-a", "+source-b", "+source-a")]
   [InlineData("+source-a", "", "+source-a")]
   [InlineData("", "+source-a", "")]
   [InlineData("+source-a", "+source-a", "+source-b")]
   public void SuccessfulMsiRequiresIdenticalSourceBuildsAcrossAllComponents(string appMetadata, string serviceMetadata, string cliMetadata)
   {
      const string requested = "0.3.2-rc.1";
      InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
         MsiUpdateInstaller.ValidateInstalledVersions(requested, path => requested + (Path.GetFileName(path) switch
         {
            "Qqq.PrintAgent.exe" => appMetadata,
            "Qqq.PrintAgent.Service.exe" => serviceMetadata,
            "qqq-print-agent.exe" => cliMetadata,
            _ => throw new InvalidOperationException("Unexpected component path."),
         })));
      Assert.Contains("Windows Installer returned success", failure.Message, StringComparison.Ordinal);
      Assert.Contains("incoherent", failure.Message, StringComparison.Ordinal);
      Assert.DoesNotContain("rolled back", failure.Message, StringComparison.OrdinalIgnoreCase);
   }



   /// <summary>The version check reads exactly the companion, service and CLI executables at their fixed locations under Program Files, with or without build metadata, and no other path.</summary>
   [Theory]
   [InlineData("0.3.2-rc.1")]
   [InlineData("0.3.2-rc.1+reviewed-source")]
   public void SuccessfulMsiChecksCompanionServiceAndCliAtTheirInstalledPaths(string installedVersion)
   {
      List<string> examined = [];
      MsiUpdateInstaller.ValidateInstalledVersions("0.3.2-rc.1", path =>
      {
         examined.Add(path);
         return installedVersion;
      });
      string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "QQQ Print Agent");
      Assert.Equal(new[]
      {
         Path.Combine(root, "App", "Qqq.PrintAgent.exe"),
         Path.Combine(root, "Service", "Qqq.PrintAgent.Service.exe"),
         Path.Combine(root, "Cli", "qqq-print-agent.exe"),
      }, examined, StringComparer.OrdinalIgnoreCase);
   }
}
