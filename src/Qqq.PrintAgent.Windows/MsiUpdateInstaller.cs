/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The machine update of the installed product. This class is its entry point: it applies the steps in
/// <see cref="MsiUpdateSteps"/> to the real folder security, the real processes and the installed product's places, and
/// it holds the update rules that need nothing from Windows (containment, links, access lists, request fields, the
/// installer's command line and exit codes), which other parts of the product use as well.
/// </summary>
public static partial class MsiUpdateInstaller
{
   /// <summary>The update steps bound to the real folder security, the real processes and the places of the installed product.</summary>
   internal static MsiUpdateSteps Installed { get; } = new(new WindowsPathSecurity(), new WindowsProcessHost(), MsiUpdatePlaces.Installed());



   /// <summary>Runs only inside the protected copied helper; never kills an installer or an application.</summary>
   [ExcludeFromCodeCoverage(Justification = "Runs the apply step against this computer: its machine state folder, the installed service's pipe, Windows Installer and the installed companion, none of which a test may touch. It only passes the call on; the step is tested in MsiUpdateSteps with fakes of IPathSecurity and IProcessHost in temporary folders, and the places it is bound to in MsiUpdatePlaces.Installed.")]
   public static Task<int> ApplyAsync(string requestPath) => Installed.ApplyAsync(requestPath);



   /// <summary>Read-only, bounded last outcome; retained for Settings/log diagnostics rather than consumed.</summary>
   [ExcludeFromCodeCoverage(Justification = "Reads the recorded outcome in this computer's machine state folder, which exists on some computers and not on others and which a test must not depend on. It only passes the call on; reading an outcome is tested in MsiUpdateSteps in a temporary folder, and the places it is bound to in MsiUpdatePlaces.Installed.")]
   public static string? ReadLastOutcomeMessage() => Installed.ReadLastOutcomeMessage();



   /// <summary>
   /// Checks every field of a request: the MSI path must sit directly inside the request's own folder and end in .msi, the
   /// process identity must be positive, the hash must be 64 hex digits, and the version must be an eligible release.
   /// </summary>
   internal static void ValidateRequest(InstallRequest request, string directory)
   {
      ValidateContainedPath(request.PackagePath, directory, allowRoot: false);
      if(!string.Equals(Path.GetDirectoryName(Path.GetFullPath(request.PackagePath)), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)
         || !string.Equals(Path.GetExtension(request.PackagePath), ".msi", StringComparison.OrdinalIgnoreCase)
         || request.ParentProcessId <= 0 || request.ParentStartTicks <= 0
         || request.Sha256 is not { Length: 64 } || !request.Sha256.All(Uri.IsHexDigit))
      {
         throw new InvalidDataException("Invalid update request fields.");
      }
      ValidateTargetVersion(request.Version);
   }



   /// <summary>Requires the version to parse as a release this updater supports and to meet the minimum, so a malformed or ancient version cannot be installed.</summary>
   internal static void ValidateTargetVersion(string version)
   {
      if(version is null || !MsiReleaseVersion.TryParse(version, out MsiReleaseVersion parsed) || !parsed.MeetsMinimum)
      {
         throw new InvalidDataException("The update version is not an eligible release.");
      }
   }



   /// <summary>
   /// What the preparing step records and the applying step re-checks: the staged MSI, its target version and SHA-256, and the
   /// companion process that asked (by id and start time), so the helper can confirm it is still the same process before it acts.
   /// </summary>
   internal sealed record InstallRequest(string PackagePath, string Version, string Sha256, int ParentProcessId, long ParentStartTicks);
}
