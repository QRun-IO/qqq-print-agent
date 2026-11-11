/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The shortcut file, its folder and its name: what the shortcut says and what marks it as ours, how it is written, which
/// folders are trusted, what a new shortcut folder may be accessed by, and how a brand name becomes a file name that
/// stays inside the folder. The naming and access-list rules need nothing from Windows and are static.
/// </summary>
internal sealed partial class AgentBrandingShortcuts
{
   /// <summary>The description every shortcut of ours carries; with the program and the empty arguments it is what marks a shortcut as ours.</summary>
   private const string OwnershipDescription = "QQQ Print Agent companion";



   /// <summary>What a run says when a file that is not ours has the name the new shortcut needs.</summary>
   private const string NameTaken = "A different shortcut already uses the requested brand name.";



   /// <summary>The all-users Start menu folder this installer owns, so a customer-branded shortcut lands in one predictable place.</summary>
   internal string ShortcutDirectory => Path.Combine(places.StartMenuPrograms, "Print Connectors");



   /// <summary>
   /// Decides whether a shortcut belongs to this application: it must launch the installed companion with no arguments and
   /// carry this installer's ownership description. Anything else is someone else's shortcut and is never overwritten or deleted.
   /// </summary>
   internal bool MatchesOwnedShortcut(string target, string arguments, string description)
      => string.Equals(target, places.InstalledExecutable, StringComparison.OrdinalIgnoreCase) && arguments.Length == 0 && string.Equals(description, OwnershipDescription, StringComparison.Ordinal);



   /// <summary>
   /// Writes the shortcut to a temporary file in the same folder and moves it to the destination, so a failure never leaves a
   /// half-written shortcut under the real name. It points only at the installed companion and carries the ownership
   /// description. Whatever the shell left under the temporary name is removed, also when the shell failed part way.
   /// <para>
   /// The destination is looked at again immediately before the move, because a file could have appeared or been
   /// replaced there since the first look. When nothing was there at the first look, the move never overwrites: a file
   /// that has appeared since is someone else's and the run is refused. When a shortcut of ours was there, it is
   /// replaced only if the second look still finds a shortcut of ours; a read-only mark on it is cleared first, since
   /// it would otherwise fail this and every later run. A file that is not ours is never overwritten or changed.
   /// </para>
   /// </summary>
   private void WriteShortcut(string path, string? icon, bool replacingOurs)
   {
      string temporary = Path.Combine(ShortcutDirectory, ".qqq-" + Guid.NewGuid().ToString("N") + ".lnk");
      // The name must match TemporaryName, which is how a later run recognises a temporary file that was left behind.
      try
      {
         shell.Write(temporary, new ShortcutLink(places.InstalledExecutable, "", OwnershipDescription, Path.GetDirectoryName(places.InstalledExecutable)!, (icon ?? places.InstalledExecutable) + ",0"));
         MsiUpdateInstaller.ValidateNoReparseComponents(ShortcutDirectory);
         if(!File.Exists(path))
         {
            File.Move(temporary, path, overwrite: false);
            return;
         }
         if(!replacingOurs || !IsOwnedShortcut(path))
         {
            throw new InvalidOperationException(NameTaken);
         }
         ClearReadOnly(path);
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



   /// <summary>
   /// The temporary shortcut files an earlier run left in the shortcut folder: a run that was killed between writing
   /// its shortcut and moving it into place, or whose temporary file could not be deleted. Only a file whose name is
   /// exactly the one a temporary file is given counts; nothing else in the folder is ever touched on this account,
   /// and a temporary name is never recorded.
   /// </summary>
   private List<string> TemporaryFiles()
      => [.. Directory.GetFiles(ShortcutDirectory, ".qqq-*.lnk").Where(path => TemporaryName().IsMatch(Path.GetFileName(path)))];



   /// <summary>Matches exactly the name a temporary shortcut file is given: ".qqq-", the 32 lowercase hexadecimal digits of a new identifier, ".lnk". The match has a one second limit.</summary>
   [GeneratedRegex("""^\.qqq-[0-9a-f]{32}\.lnk$""", RegexOptions.CultureInvariant, 1000)]
   private static partial Regex TemporaryName();



   /// <summary>
   /// Looks at an existing file under a shortcut name and reports whether it is a shortcut of ours. A link is not ours and
   /// is not opened, because this product never writes one. A file that opens and holds no shortcut at all is not ours.
   /// A shortcut that says anything but what ours says is not ours. A file that cannot be opened is not judged: nothing
   /// is known about it, so the failure is passed on, naming the shortcut, and the caller decides what that means. At a
   /// first look it stops the run before anything has changed; at the look before a delete or a replacement the file is
   /// left alone and its name stays on record.
   /// </summary>
   private bool IsOwnedShortcut(string path)
   {
      MsiUpdateInstaller.ValidateNoReparseComponents(Path.GetDirectoryName(path)!);
      if(security.IsLink(path))
      {
         return false;
      }
      ShortcutLink? link;
      try
      {
         link = shell.Read(path);
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
      {
         throw new IOException($"The shortcut \"{Path.GetFileName(path)}\" could not be read, so it cannot be known whether it belongs to this application: {exception.Message}", exception);
      }
      return link is not null && MatchesOwnedShortcut(link.Target, link.Arguments, link.Description);
   }



   /// <summary>
   /// Clears the read-only mark of a file that has just been found to be a shortcut of ours, so that it can be replaced
   /// or deleted. It is never called for a file that is not ours.
   /// </summary>
   private static void ClearReadOnly(string path)
   {
      FileAttributes attributes = File.GetAttributes(path);
      if(attributes.HasFlag(FileAttributes.ReadOnly))
      {
         File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
      }
   }



   /// <summary>Requires the install folder to be free of reparse points and to have a trusted access-control list, so a low-privilege user cannot swap the program the shortcut launches.</summary>
   private void ValidateDirectory(string directory)
   {
      MsiUpdateInstaller.ValidateNoReparseComponents(directory);
      MsiUpdateInstaller.ValidateTrustedAcl(security.ReadDirectorySecurity(directory), security.CurrentUserSid);
   }



   /// <summary>
   /// Creates the shortcut folder if it is missing with an explicit, protected access list: SYSTEM and Administrators have
   /// full control and ordinary users may only read and run. An existing folder is left as it is and validated by the caller.
   /// </summary>
   private void CreateShortcutDirectory()
   {
      MsiUpdateInstaller.ValidateNoReparseComponents(ShortcutDirectory);
      if(Directory.Exists(ShortcutDirectory))
      {
         return;
      }
      DirectorySecurity descriptor = new();
      descriptor.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
      descriptor.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
      foreach(WellKnownSidType principal in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.BuiltinUsersSid })
      {
         FileSystemRights rights = principal == WellKnownSidType.BuiltinUsersSid ? FileSystemRights.ReadAndExecute : FileSystemRights.FullControl;
         descriptor.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(principal, null), rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
      }
      security.CreateDirectory(ShortcutDirectory, descriptor);
   }



   /// <summary>Checks that a Start menu folder is not a reparse point and that its security passes the trusted-owner and no-untrusted-write test.</summary>
   private void ValidateShortcutDirectory(string directory)
   {
      MsiUpdateInstaller.ValidateNoReparseComponents(directory);
      ValidateShortcutSecurity(security.ReadDirectorySecurity(directory));
   }



   /// <summary>Refuses to run unless the caller is an elevated administrator, because branding changes machine-wide shortcuts and registry state.</summary>
   private void RequireAdministrator()
   {
      if(!security.IsAdministrator)
      {
         throw new UnauthorizedAccessException("Machine branding requires an elevated administrator.");
      }
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



   /// <summary>Shared Start menu deletion rights do not permit replacing executable content or shortcut targets.</summary>
   internal static void ValidateShortcutSecurity(FileSystemSecurity folderSecurity)
   {
      var descriptor = new RawSecurityDescriptor(folderSecurity.GetSecurityDescriptorBinaryForm(), 0);
      if(descriptor.DiscretionaryAcl is null || !TrustedShortcutPrincipal(folderSecurity.GetOwner(typeof(SecurityIdentifier))?.Value ?? ""))
      {
         throw new UnauthorizedAccessException("The shortcut directory requires a trusted owner and explicit access-control list.");
      }
      const FileSystemRights unsafeMutation = FileSystemRights.Write | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | (FileSystemRights)0x10000000 | (FileSystemRights)0x40000000;
      foreach(FileSystemAccessRule rule in folderSecurity.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
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
