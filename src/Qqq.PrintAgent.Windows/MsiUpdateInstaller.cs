/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>Stages a protected, disposable CLI copy so updating the installed binaries does not lock the helper.</summary>
public static partial class MsiUpdateInstaller
{
   /// <summary>The protected machine folder where downloaded installers, helper copies and update requests are staged; only administrators can write there.</summary>
   public static string UpdatesDirectory => Path.Combine(AgentStore.DefaultDirectory, "updates");



   /// <summary>The fixed machine-wide install folder; update trust decisions compare against it, never against a path supplied by the caller.</summary>
   private static string InstalledRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "QQQ Print Agent");



   /// <summary>The companion executable at its install path; only that process may ask for an update to be prepared.</summary>
   private static string InstalledCompanion => Path.Combine(InstalledRoot, "App", "Qqq.PrintAgent.exe");



   /// <summary>The installed command-line folder that is copied to run the update, so replacing the installed binaries does not lock the running helper.</summary>
   private static string InstalledCli => Path.Combine(InstalledRoot, "Cli");



   private const int MaximumRequestBytes = 16 * 1024;



   /// <summary>
   /// Prepares an update without applying it: checks that an administrator is asking, that the staging folder and MSI are
   /// trusted and of a sane size, that the target is an eligible release, and that the caller is the installed companion.
   /// It then copies the installed CLI into a fresh helper folder and writes a request that pins the MSI's hash and the
   /// companion's process identity. It returns the request path; nothing is installed here.
   /// </summary>
   public static string PrepareHelper(string verifiedMsiPath, string targetVersion, int parentProcessId)
   {
      RequireAdministrator();
      string package = Path.GetFullPath(verifiedMsiPath);
      string directory = Path.GetDirectoryName(package)!;
      ValidateStagingDirectory(directory);
      ValidatePackageFile(package);
      ValidateTargetVersion(targetVersion);
      using var parent = Process.GetProcessById(parentProcessId);
      string parentPath = parent.MainModule?.FileName ?? throw new InvalidOperationException("Cannot verify the update's companion process.");
      if(!string.Equals(Path.GetFullPath(parentPath), InstalledCompanion, StringComparison.OrdinalIgnoreCase))
      {
         throw new InvalidOperationException("Only the installed companion may prepare an update.");
      }
      ValidateInstalledPath(InstalledCli);
      string helper = Path.Combine(directory, "helper");
      if(Directory.Exists(helper))
      {
         throw new IOException("This update helper directory already exists. Check again to stage a fresh update.");
      }
      Directory.CreateDirectory(helper);
      CopyHelperTree(InstalledCli, helper);
      string helperExecutable = Path.Combine(helper, "qqq-print-agent.exe");
      if(!File.Exists(helperExecutable))
      {
         throw new FileNotFoundException("The installed diagnostic CLI is missing. Repair the machine MSI first.");
      }
      using FileStream content = new(package, FileMode.Open, FileAccess.Read, FileShare.Read);
      string hash = Convert.ToHexString(SHA256.HashData(content));
      var request = new InstallRequest(package, targetVersion, hash, parentProcessId, parent.StartTime.ToUniversalTime().Ticks);
      string requestPath = Path.Combine(directory, "request.json");
      using FileStream output = new(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
      JsonSerializer.Serialize(output, request, PrintApiClient.JsonOptions);
      output.Flush(flushToDisk: true);
      return requestPath;
   }



   /// <summary>
   /// Starts the staged helper copy in apply mode against a prepared request. The request is fully re-validated first and the
   /// helper must be a trusted staged file, so a replaced request or helper is refused rather than run with administrator rights.
   /// </summary>
   public static void StartHelper(string requestPath)
   {
      RequireAdministrator();
      _ = ReadRequest(requestPath);
      string helper = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(requestPath))!, "helper", "qqq-print-agent.exe");
      ValidateStagedFile(helper);
      ProcessStartInfo start = new(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(helper)! };
      start.ArgumentList.Add("--apply-update");
      start.ArgumentList.Add(Path.GetFullPath(requestPath));
      using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the staged update helper.");
   }



   /// <summary>
   /// Copies the installed CLI folder recursively into the helper folder. Every folder and file must have a trusted access
   /// list and none may be a link or junction, so a low-privilege user cannot slip a program into the copy that later runs elevated.
   /// </summary>
   private static void CopyHelperTree(string source, string destination)
   {
      ValidateNoReparseComponents(source);
      ValidateTrustedAcl(new DirectoryInfo(source).GetAccessControl());
      foreach(string entry in Directory.EnumerateFileSystemEntries(source))
      {
         FileAttributes attributes = File.GetAttributes(entry);
         if(attributes.HasFlag(FileAttributes.ReparsePoint))
         {
            throw new IOException("Update helper source cannot contain symbolic links or junctions.");
         }
         string target = Path.Combine(destination, Path.GetFileName(entry));
         if(attributes.HasFlag(FileAttributes.Directory))
         {
            Directory.CreateDirectory(target);
            CopyHelperTree(entry, target);
         }
         else
         {
            ValidateTrustedAcl(new FileInfo(entry).GetAccessControl());
            File.Copy(entry, target, overwrite: false);
         }
      }
   }



   /// <summary>
   /// Reads and validates an update request: a trusted staged file named exactly request.json and no larger than 16 KiB, with
   /// fields that pass <c>ValidateRequest</c> and a package file that is still a valid MSI. Anything else is refused as an invalid request.
   /// </summary>
   private static InstallRequest ReadRequest(string requestPath)
   {
      string path = Path.GetFullPath(requestPath);
      ValidateStagedFile(path);
      if(!string.Equals(Path.GetFileName(path), "request.json", StringComparison.Ordinal) || new FileInfo(path).Length > MaximumRequestBytes)
      {
         throw new InvalidDataException("Invalid update request file.");
      }
      using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
      InstallRequest request = JsonSerializer.Deserialize<InstallRequest>(stream, PrintApiClient.JsonOptions) ?? throw new InvalidDataException("Empty update request.");
      ValidateRequest(request, Path.GetDirectoryName(path)!);
      ValidatePackageFile(request.PackagePath);
      return request;
   }



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
   private static void ValidateTargetVersion(string version)
   {
      if(version is null || !MsiReleaseVersion.TryParse(version, out MsiReleaseVersion parsed) || !parsed.MeetsMinimum)
      {
         throw new InvalidDataException("The update version is not an eligible release.");
      }
   }



   /// <summary>Requires the staged package to be a trusted file with an .msi extension and a size between one byte and 256 MiB.</summary>
   private static void ValidatePackageFile(string package)
   {
      ValidateStagedFile(package);
      long length = new FileInfo(package).Length;
      if(length is <= 0 or > 256L * 1024 * 1024 || !string.Equals(Path.GetExtension(package), ".msi", StringComparison.OrdinalIgnoreCase))
      {
         throw new InvalidDataException("The staged MSI has an invalid size or extension.");
      }
   }



   /// <summary>
   /// What the preparing step records and the applying step re-checks: the staged MSI, its target version and SHA-256, and the
   /// companion process that asked (by id and start time), so the helper can confirm it is still the same process before it acts.
   /// </summary>
   internal sealed record InstallRequest(string PackagePath, string Version, string Sha256, int ParentProcessId, long ParentStartTicks);
}
