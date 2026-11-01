/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.AccessControl;

namespace Qqq.PrintAgent.Windows;

public sealed partial class QqqBrowserCallback
{
   internal static string CompanionSibling(string executable)
   {
      string full = Path.GetFullPath(executable);
      DirectoryInfo directory = new(Path.GetDirectoryName(full)!);
      if(!string.Equals(Path.GetFileName(full), "qqq-print-agent.exe", StringComparison.OrdinalIgnoreCase)
         || !string.Equals(directory.Name, "Cli", StringComparison.OrdinalIgnoreCase) || directory.Parent is null)
      {
         throw new UnauthorizedAccessException("Browser callbacks require the installed CLI location.");
      }
      return Path.Combine(directory.Parent.FullName, "App", "Qqq.PrintAgent.exe");
   }

   private static string InstalledCompanionPath()
   {
      string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the installed callback helper.");
      string companion = CompanionSibling(executable);
      ValidateCallbackInstallation(executable);
      ValidateCallbackInstallation(companion);
      return companion;
   }

   private static void ValidateCallbackInstallation(string path)
   {
      string boundary = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
      MsiUpdateInstaller.ValidateContainedPath(path, boundary, allowRoot: false);
      MsiUpdateInstaller.ValidateNoReparseComponents(path);
      string? current = Path.GetFullPath(path);
      while(current is not null && !string.Equals(current, Path.GetDirectoryName(boundary), StringComparison.OrdinalIgnoreCase))
      {
         FileSystemSecurity security = Directory.Exists(current) ? new DirectoryInfo(current).GetAccessControl() : new FileInfo(current).GetAccessControl();
         MsiUpdateInstaller.ValidateTrustedAcl(security, administratorSid: null);
         current = Path.GetDirectoryName(current);
      }
   }
}
