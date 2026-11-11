/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The rules of the applying step that need nothing from Windows but a file: verifying and holding the staged MSI, the
/// command line Windows Installer is started with, what its exit code means and when an installed version matches. The
/// step itself, which starts the installer and runs only inside the staged helper copy, is
/// <see cref="MsiUpdateSteps.ApplyAsync"/>.
/// </summary>
public static partial class MsiUpdateInstaller
{
   /// <summary>
   /// Opens the staged MSI for reading with sharing that blocks writes and deletes, and checks its size and SHA-256 against the
   /// request in constant time. The caller keeps the handle open until the installer exits, so the file cannot be swapped
   /// between the check and the install.
   /// </summary>
   internal static FileStream OpenVerifiedPackage(string path, string expectedHash)
   {
      FileStream content = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
      try
      {
         if(content.Length is <= 0 or > 256L * 1024 * 1024
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(content), Convert.FromHexString(expectedHash)))
         {
            throw new InvalidDataException("The staged update changed or failed its checksum. Download it again.");
         }
         return content;
      }
      catch
      {
         content.Dispose();
         throw;
      }
   }



   /// <summary>Builds the msiexec start: the system msiexec, passive display, no automatic restart and a verbose log in the protected folder. Arguments go in a list, never a shell string.</summary>
   internal static ProcessStartInfo CreateInstallerStart(string package, string logPath)
   {
      ProcessStartInfo start = new(Path.Combine(Environment.SystemDirectory, "msiexec.exe")) { UseShellExecute = false, CreateNoWindow = true };
      foreach(string argument in new[] { "/i", package, "/passive", "/norestart", "/L*v", logPath })
      {
         start.ArgumentList.Add(argument);
      }
      return start;
   }



   /// <summary>
   /// Turns an installer exit code into an honest message for the operator: success, restart required (3010, which is not
   /// full success), operator cancellation (1602), or an unreported failure that points at the private log.
   /// </summary>
   internal static string DescribeExitCode(int code) => code switch
   {
      0 => "Update installed successfully. Open the companion to verify service status.",
      3010 => "Update installed, but Windows requires a restart to finish replacing files. Restart Windows before treating the new version as fully active.",
      1602 => "Windows Installer canceled the update. The previous installation may remain; inspect the version and service status before retrying.",
      _ => "Windows Installer did not report a completed update (exit " + code.ToString(CultureInfo.InvariantCulture) + "). Inspect the private installation log and service status before retrying.",
   };



   /// <summary>True when the installed product version, ignoring the build metadata after "+", is exactly the requested release.</summary>
   internal static bool InstalledVersionMatches(string? productVersion, string requestedVersion)
      => productVersion is not null && string.Equals(productVersion.Split('+')[0], requestedVersion, StringComparison.Ordinal);



   /// <summary>Checks the installed payload at the installed product's paths after MSI success; see <see cref="MsiUpdateSteps.ValidateInstalledVersions"/>.</summary>
   internal static void ValidateInstalledVersions(string requestedVersion, Func<string, string?> readProductVersion) => Installed.ValidateInstalledVersions(requestedVersion, readProductVersion);
}
