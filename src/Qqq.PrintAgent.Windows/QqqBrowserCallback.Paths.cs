/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using System.Security.AccessControl;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Installation path checks of the browser callback. The callback may only be forwarded to the installed companion, so the helper must
/// run from the installed CLI folder and both programs must sit in trusted, link-free locations under Program Files.
/// </summary>
public sealed partial class QqqBrowserCallback
{
   /// <summary>Derives the companion's path from the CLI's own path, accepting only the installed layout (a Cli folder next to an App folder), so the callback address is never handed to a program found anywhere else.</summary>
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



   /// <summary>The companion's path for this process as installed on this computer; see the overload for what is checked.</summary>
   [ExcludeFromCodeCoverage(Justification = "Binds the installation check to the path of the running process, this computer's Program Files folder and the real security descriptors under it. A test does not run from the installed location. It only passes the call on; the check itself is tested through the overload below with temporary folders and a fake of IPathSecurity.")]
   private static string InstalledCompanionPath()
      => InstalledCompanionPath(Environment.ProcessPath, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), new WindowsPathSecurity());



   /// <summary>Returns the companion's path after validating that both this helper and the companion are installed in trusted locations, and throws otherwise.</summary>
   internal static string InstalledCompanionPath(string? processPath, string programFiles, IPathSecurity security)
   {
      string executable = processPath ?? throw new InvalidOperationException("Cannot locate the installed callback helper.");
      string companion = CompanionSibling(executable);
      ValidateCallbackInstallation(executable, programFiles, security);
      ValidateCallbackInstallation(companion, programFiles, security);
      return companion;
   }



   /// <summary>Requires a path to be inside Program Files with no link or junction, and every folder from it up to Program Files to have a trusted access list, so an ordinary user cannot substitute the program that receives the callback.</summary>
   private static void ValidateCallbackInstallation(string path, string boundary, IPathSecurity security)
   {
      MsiUpdateInstaller.ValidateContainedPath(path, boundary, allowRoot: false);
      MsiUpdateInstaller.ValidateNoReparseComponents(path);
      string? current = Path.GetFullPath(path);
      while(current is not null && !string.Equals(current, Path.GetDirectoryName(boundary), StringComparison.OrdinalIgnoreCase))
      {
         FileSystemSecurity descriptor = Directory.Exists(current) ? security.ReadDirectorySecurity(current) : security.ReadFileSecurity(current);
         MsiUpdateInstaller.ValidateTrustedAcl(descriptor, administratorSid: null);
         current = Path.GetDirectoryName(current);
      }
   }
}
