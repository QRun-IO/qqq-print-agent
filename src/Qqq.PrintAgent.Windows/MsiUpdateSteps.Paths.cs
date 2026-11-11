/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.AccessControl;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Path and permission checks of the updater. The staged files later run with administrator rights, so each path must stay
/// inside the protected folders, contain no link or junction, and be owned and writable only by trusted accounts. Every
/// failure throws; nothing is repaired automatically.
/// </summary>
internal sealed partial class MsiUpdateSteps
{
   /// <summary>Requires installed protected state; permits a not-yet-created staging child.</summary>
   internal void ValidateStagingDirectory(string path)
   {
      RequireAdministrator();
      places.EnsureProtectedState(path);
      string directory = Path.GetFullPath(path);
      MsiUpdateInstaller.ValidateContainedPath(directory, UpdatesDirectory, allowRoot: true);
      if(!Directory.Exists(places.StateDirectory))
      {
         throw new InvalidOperationException("Install the machine MSI before staging updates.");
      }
      MsiUpdateInstaller.ValidateNoReparseComponents(directory);
      string? current = directory;
      while(current is not null && MsiUpdateInstaller.IsContained(current, places.StateDirectory))
      {
         if(File.Exists(current))
         {
            throw new IOException("An update directory path points to a file.");
         }
         if(Directory.Exists(current))
         {
            ValidateTrustedAcl(security.ReadDirectorySecurity(current));
         }
         current = Path.GetDirectoryName(current);
      }
   }



   /// <summary>Requires a staged file to sit in a validated staging folder, be free of links, exist, and have a trusted access list.</summary>
   private void ValidateStagedFile(string path)
   {
      ValidateStagingDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
      MsiUpdateInstaller.ValidateNoReparseComponents(path);
      if(!File.Exists(path))
      {
         throw new FileNotFoundException("A required staged update file is missing.");
      }
      ValidateTrustedAcl(security.ReadFileSecurity(path));
   }



   /// <summary>Requires an installed program path to be inside Program Files, free of links, and trusted at every level up to Program Files, so a user-writable folder cannot supply the program that is run.</summary>
   private void ValidateInstalledPath(string path)
   {
      MsiUpdateInstaller.ValidateNoReparseComponents(path);
      string? current = Path.GetFullPath(path);
      MsiUpdateInstaller.ValidateContainedPath(current, places.ProgramFiles, allowRoot: false);
      while(current is not null && MsiUpdateInstaller.IsContained(current, places.ProgramFiles))
      {
         ValidateTrustedAcl(Directory.Exists(current) ? security.ReadDirectorySecurity(current) : security.ReadFileSecurity(current));
         current = Path.GetDirectoryName(current);
      }
   }



   /// <summary>Refuses to continue unless the caller is an elevated administrator, and says no installer was started so the operator knows nothing changed.</summary>
   private void RequireAdministrator()
   {
      if(!security.IsAdministrator)
      {
         throw new UnauthorizedAccessException("Machine updates require an elevated administrator. No installer was started.");
      }
   }



   /// <summary>Checks a path's security against trusted accounts, treating the current administrator as one of them.</summary>
   private void ValidateTrustedAcl(FileSystemSecurity descriptor) => MsiUpdateInstaller.ValidateTrustedAcl(descriptor, security.CurrentUserSid);
}
