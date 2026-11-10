/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Qqq.PrintAgent.Core;


namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Installer-only branding of an owned all-users shortcut; application identity never changes. This class is the entry
/// point for the installed product: it applies the rules in <see cref="AgentBrandingShortcuts"/> to the real shell, folder
/// security, registry and install paths. The naming and access-list rules that need nothing from Windows live here as well.
/// </summary>
public static class AgentBrandingInstaller
{
   private static readonly AgentBrandingShortcuts Installed = new(new WindowsShortcutShell(), new WindowsPathSecurity(), new RegistryShortcutRecord(),
      new BrandingPlaces(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "QQQ Print Agent", "App", "Qqq.PrintAgent.exe"),
         Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AgentStore.DefaultDirectory, MachineStateDirectory.EnsureForPath, RuntimeBranding.LoadMachine));



   /// <summary>
   /// Creates or renames the all-users Start menu shortcut to the customer's brand name; see
   /// <see cref="AgentBrandingShortcuts.ApplyInstalledBranding"/> for what it requires and refuses.
   /// </summary>
   [ExcludeFromCodeCoverage(Justification = "Runs the branding rules against this computer's all-users Start menu, registry and install folder, which a test must not change and which need administrator rights. It only passes the call on; the rules it runs are tested in AgentBrandingShortcuts with fakes.")]
   public static void ApplyInstalledBranding() => Installed.ApplyInstalledBranding();



   /// <summary>Removes the shortcut recorded by the last branding run, and only if it is still the one this installer created; see <see cref="AgentBrandingShortcuts.RemoveInstalledShortcut"/>.</summary>
   [ExcludeFromCodeCoverage(Justification = "Runs the branding rules against this computer's all-users Start menu, registry and install folder, which a test must not change and which need administrator rights. It only passes the call on; the rules it runs are tested in AgentBrandingShortcuts with fakes.")]
   public static void RemoveInstalledShortcut() => Installed.RemoveInstalledShortcut();



   /// <summary>
   /// Turns a customer's display name into a safe shortcut file name. Invalid characters and reserved device names are
   /// replaced, the length is capped at 100, and when anything was altered a short hash of the original is appended so two
   /// different names can never collapse to the same file.
   /// </summary>
   internal static string ShortcutFileName(string label)
   {
      string trimmed = label.Trim();
      if(trimmed.Length == 0)
      {
         trimmed = "Print Connector";
      }
      char[] invalid = Path.GetInvalidFileNameChars();
      string clean = new(trimmed.Select(character => invalid.Contains(character) || char.IsControl(character) ? '-' : character).ToArray());
      clean = clean.Trim(' ', '.');
      string stem = clean.Split('.')[0];
      if(clean.Length == 0 || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
      {
         clean = "Print Connector " + clean;
      }
      bool changed = !string.Equals(clean, trimmed, StringComparison.Ordinal) || clean.Length > 100;
      if(clean.Length > 100)
      {
         clean = clean[..100].TrimEnd(' ', '.');
      }
      string suffix = changed ? "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed)))[..12] : "";
      return clean + suffix + ".lnk";
   }



   /// <summary>
   /// Resolves a shortcut file name inside the owned folder. The name must be a plain, bounded .lnk file name with no
   /// directory part, and the resulting full path must stay inside the folder, so a stored or computed name cannot reach elsewhere.
   /// </summary>
   internal static string ShortcutPath(string directory, string filename)
   {
      if(filename.Length is < 5 or > 140 || !string.Equals(Path.GetFileName(filename), filename, StringComparison.Ordinal) || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !filename.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
      {
         throw new InvalidDataException("Invalid owned shortcut filename.");
      }
      string path = Path.GetFullPath(Path.Combine(directory, filename));
      MsiUpdateInstaller.ValidateContainedPath(path, directory, allowRoot: false);
      return path;
   }



   /// <summary>Whether a shortcut that says this belongs to this application as installed; see <see cref="AgentBrandingShortcuts.MatchesOwnedShortcut"/>.</summary>
   internal static bool MatchesOwnedShortcut(string target, string arguments, string description) => Installed.MatchesOwnedShortcut(target, arguments, description);



   /// <summary>Shared Start menu deletion rights do not permit replacing executable content or shortcut targets.</summary>
   internal static void ValidateShortcutSecurity(FileSystemSecurity security)
   {
      var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
      if(descriptor.DiscretionaryAcl is null || !TrustedShortcutPrincipal(security.GetOwner(typeof(SecurityIdentifier))?.Value ?? ""))
      {
         throw new UnauthorizedAccessException("The shortcut directory requires a trusted owner and explicit access-control list.");
      }
      const FileSystemRights unsafeMutation = FileSystemRights.Write | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | (FileSystemRights)0x10000000 | (FileSystemRights)0x40000000;
      foreach(FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
      {
         bool trustedCreator = rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly) && string.Equals(rule.IdentityReference.Value, "S-1-3-0", StringComparison.Ordinal);
         if(rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & unsafeMutation) != default && !TrustedShortcutPrincipal(rule.IdentityReference.Value) && !trustedCreator)
         {
            throw new UnauthorizedAccessException("The shortcut directory permits untrusted content or permission changes.");
         }
      }
   }



   /// <summary>True for the accounts allowed to own or write the folder: SYSTEM, Administrators and the Windows installer service. Anyone else could redirect a shortcut.</summary>
   private static bool TrustedShortcutPrincipal(string sid)
      => sid is "S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
}
