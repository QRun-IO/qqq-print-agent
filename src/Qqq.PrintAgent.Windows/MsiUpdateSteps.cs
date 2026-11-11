/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The steps of a machine update and every check that guards them: who may ask, which folders and files are trusted,
/// which program is copied and started, what the request says and what Windows Installer's answer means. Owners and
/// access lists come through <see cref="IPathSecurity"/>, processes through <see cref="IProcessHost"/>, and the folders,
/// the pipe, the log and the clock are given in <see cref="MsiUpdatePlaces"/>, so every step can be run against fakes in
/// temporary folders without starting Windows Installer. The rules that need nothing from Windows (containment, links,
/// access lists, request fields, the installer's command line and exit codes) stay in <see cref="MsiUpdateInstaller"/>.
/// This part stages a protected, disposable CLI copy so updating the installed binaries does not lock the helper.
/// </summary>
internal sealed partial class MsiUpdateSteps(IPathSecurity security, IProcessHost processes, MsiUpdatePlaces places)
{
   /// <summary>The largest update request or recorded outcome that is read; anything larger is not one this product wrote.</summary>
   private const int MaximumRequestBytes = 16 * 1024;



   /// <summary>The protected machine folder where downloaded installers, helper copies and update requests are staged; only administrators can write there.</summary>
   internal string UpdatesDirectory => places.UpdatesDirectory;



   /// <summary>
   /// True only for the MSI-installed companion (running from its Program Files path with the machine state folder present).
   /// A development or portable copy cannot replace the service and CLI together, so it is never offered an in-app update.
   /// </summary>
   internal bool RunsAsInstalledCompanion
      => string.Equals(processes.CurrentPath, places.InstalledCompanion, StringComparison.OrdinalIgnoreCase) && Directory.Exists(places.StateDirectory);



   /// <summary>The id of the process these steps run in, which is the companion's when it prepares an update.</summary>
   internal int CurrentProcessId => processes.CurrentId;



   /// <summary>
   /// Prepares an update without applying it: checks that an administrator is asking, that the staging folder and MSI are
   /// trusted and of a sane size, that the target is an eligible release, and that the caller is the installed companion.
   /// It then copies the installed CLI into a fresh helper folder and writes a request that pins the MSI's hash and the
   /// companion's process identity. It returns the request path; nothing is installed here.
   /// </summary>
   internal string PrepareHelper(string verifiedMsiPath, string targetVersion, int parentProcessId)
   {
      RequireAdministrator();
      string package = Path.GetFullPath(verifiedMsiPath);
      string directory = Path.GetDirectoryName(package)!;
      ValidateStagingDirectory(directory);
      ValidatePackageFile(package);
      MsiUpdateInstaller.ValidateTargetVersion(targetVersion);
      using IRunningProcess parent = processes.Open(parentProcessId);
      string parentPath = parent.ProgramPath ?? throw new InvalidOperationException("Cannot verify the update's companion process.");
      if(!string.Equals(Path.GetFullPath(parentPath), places.InstalledCompanion, StringComparison.OrdinalIgnoreCase))
      {
         throw new InvalidOperationException("Only the installed companion may prepare an update.");
      }
      ValidateInstalledPath(places.InstalledCli);
      string helper = Path.Combine(directory, "helper");
      if(Directory.Exists(helper))
      {
         throw new IOException("This update helper directory already exists. Check again to stage a fresh update.");
      }
      Directory.CreateDirectory(helper);
      CopyHelperTree(places.InstalledCli, helper);
      string helperExecutable = Path.Combine(helper, "qqq-print-agent.exe");
      if(!File.Exists(helperExecutable))
      {
         throw new FileNotFoundException("The installed command line tool is missing. Repair the machine MSI first.");
      }
      using FileStream content = new(package, FileMode.Open, FileAccess.Read, FileShare.Read);
      string hash = Convert.ToHexString(SHA256.HashData(content));
      var request = new MsiUpdateInstaller.InstallRequest(package, targetVersion, hash, parentProcessId, parent.StartTicks);
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
   internal void StartHelper(string requestPath)
   {
      RequireAdministrator();
      _ = ReadRequest(requestPath);
      string helper = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(requestPath))!, "helper", "qqq-print-agent.exe");
      ValidateStagedFile(helper);
      ProcessStartInfo start = new(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(helper)! };
      start.ArgumentList.Add("--apply-update");
      start.ArgumentList.Add(Path.GetFullPath(requestPath));
      using IRunningProcess process = processes.Start(start) ?? throw new InvalidOperationException("Could not start the staged update helper.");
   }



   /// <summary>
   /// Copies the installed CLI folder recursively into the helper folder. Every folder and file must have a trusted access
   /// list and none may be a link or junction, so a low-privilege user cannot slip a program into the copy that later runs elevated.
   /// </summary>
   private void CopyHelperTree(string source, string destination)
   {
      MsiUpdateInstaller.ValidateNoReparseComponents(source);
      ValidateTrustedAcl(security.ReadDirectorySecurity(source));
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
            ValidateTrustedAcl(security.ReadFileSecurity(entry));
            File.Copy(entry, target, overwrite: false);
         }
      }
   }



   /// <summary>
   /// Reads and validates an update request: a trusted staged file named exactly request.json and no larger than 16 KiB, with
   /// fields that pass <see cref="MsiUpdateInstaller.ValidateRequest"/> and a package file that is still a valid MSI. Anything else is refused as an invalid request.
   /// </summary>
   private MsiUpdateInstaller.InstallRequest ReadRequest(string requestPath)
   {
      string path = Path.GetFullPath(requestPath);
      ValidateStagedFile(path);
      if(!string.Equals(Path.GetFileName(path), "request.json", StringComparison.Ordinal) || new FileInfo(path).Length > MaximumRequestBytes)
      {
         throw new InvalidDataException("Invalid update request file.");
      }
      using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
      MsiUpdateInstaller.InstallRequest request = JsonSerializer.Deserialize<MsiUpdateInstaller.InstallRequest>(stream, PrintApiClient.JsonOptions) ?? throw new InvalidDataException("Empty update request.");
      MsiUpdateInstaller.ValidateRequest(request, Path.GetDirectoryName(path)!);
      ValidatePackageFile(request.PackagePath);
      return request;
   }



   /// <summary>Requires the staged package to be a trusted file with an .msi extension and a size between one byte and 256 MiB.</summary>
   private void ValidatePackageFile(string package)
   {
      ValidateStagedFile(package);
      long length = new FileInfo(package).Length;
      if(length is <= 0 or > 256L * 1024 * 1024 || !string.Equals(Path.GetExtension(package), ".msi", StringComparison.OrdinalIgnoreCase))
      {
         throw new InvalidDataException("The staged MSI has an invalid size or extension.");
      }
   }
}
