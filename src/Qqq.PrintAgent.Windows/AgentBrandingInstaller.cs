/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Qqq.PrintAgent.Core;


namespace Qqq.PrintAgent.Windows;

/// <summary>Installer-only branding of an owned all-users shortcut; application identity never changes.</summary>
public static class AgentBrandingInstaller
{
   private const string RegistryPath = @"SOFTWARE\QRunIO\PrintAgent";
   private const string OwnershipDescription = "QQQ Print Agent companion";



   /// <summary>The companion executable the shortcut must launch: the fixed machine-wide install path, never a configurable one.</summary>
   private static string InstalledExecutable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "QQQ Print Agent", "App", "Qqq.PrintAgent.exe");



   /// <summary>The all-users Start menu folder this installer owns, so a customer-branded shortcut lands in one predictable place.</summary>
   private static string ShortcutDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Print Connectors");



   /// <summary>
   /// Creates or renames the all-users Start menu shortcut to the customer's brand name. It requires an administrator,
   /// verifies the install and shortcut folders (no reparse points, trusted owner, no untrusted write access), and refuses to
   /// replace a shortcut it does not own. The previous brand's shortcut is removed only after the new one is written, and the
   /// chosen file name is recorded so a later run or uninstall can find exactly what this installer made.
   /// </summary>
   public static void ApplyInstalledBranding()
   {
      RequireAdministrator();
      MachineStateDirectory.EnsureForPath(AgentStore.DefaultDirectory);
      ValidateDirectory(Path.GetDirectoryName(InstalledExecutable)!);
      if(!File.Exists(InstalledExecutable))
      {
         throw new FileNotFoundException("The installed companion is missing.");
      }
      MsiUpdateInstaller.ValidateNoReparseComponents(InstalledExecutable);
      ValidateShortcutDirectory(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
      CreateShortcutDirectory();
      ValidateShortcutDirectory(ShortcutDirectory);
      var branding = RuntimeBranding.LoadMachine();
      string filename = ShortcutFileName(branding.DisplayName ?? AppIdentity.DefaultInfo.DisplayName);
      string destination = ShortcutPath(ShortcutDirectory, filename);
      using RegistryKey key = Registry.LocalMachine.CreateSubKey(RegistryPath);
      string? previous = key.GetValue("LastShortcutName") as string;
      if(File.Exists(destination) && !IsOwnedShortcut(destination))
      {
         throw new InvalidOperationException("A different shortcut already uses the requested brand name.");
      }
      WriteShortcut(destination, branding.IconPath);
      if(previous is not null && !string.Equals(previous, filename, StringComparison.OrdinalIgnoreCase))
      {
         RemoveOwnedShortcut(ShortcutPath(ShortcutDirectory, previous));
      }
      key.SetValue("LastShortcutName", filename, RegistryValueKind.String);
   }



   /// <summary>
   /// Removes the shortcut recorded by the last branding run, and only if it is still the one this installer created and
   /// its folder is still safe. If nothing was recorded it does nothing, so uninstalling never deletes a user's own shortcut.
   /// </summary>
   public static void RemoveInstalledShortcut()
   {
      RequireAdministrator();
      using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryPath, writable: true);
      string? filename = key?.GetValue("LastShortcutName") as string;
      if(filename is null)
      {
         return;
      }
      if(Directory.Exists(ShortcutDirectory))
      {
         ValidateShortcutDirectory(ShortcutDirectory);
         RemoveOwnedShortcut(ShortcutPath(ShortcutDirectory, filename));
      }
      key!.DeleteValue("LastShortcutName", throwOnMissingValue: false);
   }



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



   /// <summary>
   /// Decides whether a shortcut belongs to this application: it must launch the installed companion with no arguments and
   /// carry this installer's ownership description. Anything else is someone else's shortcut and is never overwritten or deleted.
   /// </summary>
   internal static bool MatchesOwnedShortcut(string target, string arguments, string description)
      => string.Equals(target, InstalledExecutable, StringComparison.OrdinalIgnoreCase) && arguments.Length == 0 && string.Equals(description, OwnershipDescription, StringComparison.Ordinal);



   /// <summary>
   /// Writes the shortcut to a temporary file in the same folder and moves it over the destination, so a crash never leaves a
   /// half-written shortcut under the real name. It points only at the installed companion and carries the ownership description.
   /// </summary>
   private static void WriteShortcut(string path, string? icon)
   {
      string temporary = Path.Combine(ShortcutDirectory, ".qqq-" + Guid.NewGuid().ToString("N") + ".lnk");
      try
      {
         WithShortcut(temporary, shortcut =>
         {
            SetProperty(shortcut, "TargetPath", InstalledExecutable);
            SetProperty(shortcut, "Arguments", "");
            SetProperty(shortcut, "WorkingDirectory", Path.GetDirectoryName(InstalledExecutable)!);
            SetProperty(shortcut, "Description", OwnershipDescription);
            SetProperty(shortcut, "IconLocation", (icon ?? InstalledExecutable) + ",0");
            Invoke(shortcut, "Save", BindingFlags.InvokeMethod, null);
         });
         MsiUpdateInstaller.ValidateNoReparseComponents(path);
         File.Move(temporary, path, overwrite: true);
      }
      finally
      {
         if(File.Exists(temporary))
         {
            File.Delete(temporary);
         }
      }
   }



   /// <summary>Reads an existing shortcut through Windows shortcut automation and reports whether it matches this application's ownership marks.</summary>
   private static bool IsOwnedShortcut(string path)
   {
      MsiUpdateInstaller.ValidateNoReparseComponents(path);
      bool owned = false;
      WithShortcut(path, shortcut => owned = MatchesOwnedShortcut(GetProperty(shortcut, "TargetPath"), GetProperty(shortcut, "Arguments"), GetProperty(shortcut, "Description")));
      return owned;
   }



   /// <summary>
   /// Deletes a shortcut only when it is still ours. A missing file is fine; a file that no longer matches the ownership marks
   /// is left untouched and reported, because deleting a user's shortcut would be worse than leaving a stale one.
   /// </summary>
   private static void RemoveOwnedShortcut(string path)
   {
      if(!File.Exists(path))
      {
         return;
      }
      if(!IsOwnedShortcut(path))
      {
         throw new InvalidOperationException("The tracked shortcut no longer belongs to this application; it was left unchanged.");
      }
      File.Delete(path);
   }



   /// <summary>
   /// Runs an action against a shortcut object created through the WScript.Shell automation object. The COM objects are
   /// always released afterwards, even when the action fails, so installer runs do not leak COM references.
   /// </summary>
   private static void WithShortcut(string path, Action<object> action)
   {
      object? shell = null;
      object? shortcut = null;
      try
      {
         Type type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows shortcut automation is unavailable.");
         shell = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Windows shortcut automation could not start.");
         shortcut = Invoke(shell, "CreateShortcut", BindingFlags.InvokeMethod, [path]) ?? throw new InvalidOperationException("Windows could not open the shortcut.");
         action(shortcut);
      }
      finally
      {
         if(shortcut is not null && Marshal.IsComObject(shortcut))
         {
            Marshal.FinalReleaseComObject(shortcut);
         }
         if(shell is not null && Marshal.IsComObject(shell))
         {
            Marshal.FinalReleaseComObject(shell);
         }
      }
   }



   /// <summary>Calls a member of a late-bound COM object with the invariant culture, since shortcut automation has no compile-time type.</summary>
   private static object? Invoke(object instance, string name, BindingFlags flags, object[]? arguments)
      => instance.GetType().InvokeMember(name, flags, null, instance, arguments, CultureInfo.InvariantCulture);



   /// <summary>Reads a string property of a shortcut, treating a missing value as empty.</summary>
   private static string GetProperty(object instance, string name) => Invoke(instance, name, BindingFlags.GetProperty, null) as string ?? "";



   /// <summary>Sets a string property of a shortcut.</summary>
   private static void SetProperty(object instance, string name, string value) => Invoke(instance, name, BindingFlags.SetProperty, [value]);



   /// <summary>Requires the install folder to be free of reparse points and to have a trusted access-control list, so a low-privilege user cannot swap the program the shortcut launches.</summary>
   private static void ValidateDirectory(string directory)
   {
      MsiUpdateInstaller.ValidateNoReparseComponents(directory);
      using var identity = WindowsIdentity.GetCurrent();
      MsiUpdateInstaller.ValidateTrustedAcl(new DirectoryInfo(directory).GetAccessControl(), identity.User?.Value);
   }



   /// <summary>
   /// Creates the shortcut folder if it is missing with an explicit, protected access list: SYSTEM and Administrators have
   /// full control and ordinary users may only read and run. An existing folder is left as it is and validated by the caller.
   /// </summary>
   private static void CreateShortcutDirectory()
   {
      MsiUpdateInstaller.ValidateNoReparseComponents(ShortcutDirectory);
      if(Directory.Exists(ShortcutDirectory))
      {
         return;
      }
      DirectorySecurity security = new();
      security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
      security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
      foreach(WellKnownSidType principal in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.BuiltinUsersSid })
      {
         FileSystemRights rights = principal == WellKnownSidType.BuiltinUsersSid ? FileSystemRights.ReadAndExecute : FileSystemRights.FullControl;
         security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(principal, null), rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
      }
      new DirectoryInfo(ShortcutDirectory).Create(security);
   }



   /// <summary>Checks that a Start menu folder is not a reparse point and that its security passes the trusted-owner and no-untrusted-write test.</summary>
   private static void ValidateShortcutDirectory(string directory)
   {
      MsiUpdateInstaller.ValidateNoReparseComponents(directory);
      ValidateShortcutSecurity(new DirectoryInfo(directory).GetAccessControl());
   }



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



   /// <summary>Refuses to run unless the caller is an elevated administrator, because branding changes machine-wide shortcuts and registry state.</summary>
   private static void RequireAdministrator()
   {
      using var identity = WindowsIdentity.GetCurrent();
      if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
      {
         throw new UnauthorizedAccessException("Machine branding requires an elevated administrator.");
      }
   }
}
