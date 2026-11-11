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
   /// It returns nothing when the new shortcut is in place. When a shortcut of ours was under the name at the first
   /// look and cannot be opened for the second look or replaced now, after the tries a file in use is given, the new
   /// shortcut is given up: the file that is there is left exactly as it is, and the kind of the failure is returned
   /// for the caller to note. Every other failure is thrown, as it is when nothing was there at the first look,
   /// because then there would be no shortcut under the name at all.
   /// </para>
   /// </summary>
   private string? WriteShortcut(string path, string? icon, bool replacingOurs)
   {
      string temporary = Path.Combine(ShortcutDirectory, ".qqq-" + Guid.NewGuid().ToString("N") + ".lnk");
      // The name must match TemporaryName, which is how a later run recognises a temporary file that was left behind.
      try
      {
         shell.Write(temporary, new ShortcutLink(places.InstalledExecutable, "", OwnershipDescription, Path.GetDirectoryName(places.InstalledExecutable)!, (icon ?? places.InstalledExecutable) + ",0"));
         MsiUpdateInstaller.ValidateNoReparseComponents(ShortcutDirectory);
         try
         {
            WhileHeld(() => PutInPlace(temporary, path, replacingOurs));
            return null;
         }
         catch(Exception exception) when(replacingOurs && exception is IOException or UnauthorizedAccessException && File.Exists(path))
         {
            return Kind(exception);
         }
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
   /// One try at moving the finished shortcut to the destination. The destination is looked at again immediately
   /// before the move, because a file could have appeared or been replaced there since the first look, or since the
   /// try before this one. When nothing is there, the move never overwrites. When a file is there, it is replaced
   /// only if a shortcut of ours was there at the first look and this look still finds a shortcut of ours; a file that
   /// has appeared since, or that is not ours, is never overwritten or changed, and the run is refused. A read-only
   /// mark on a shortcut of ours is cleared first, since it would otherwise defeat this and every later run. A file
   /// that cannot be opened or replaced throws as the file system does.
   /// </summary>
   private void PutInPlace(string temporary, string path, bool replacingOurs)
   {
      if(!File.Exists(path))
      {
         File.Move(temporary, path, overwrite: false);
         return;
      }
      if(!replacingOurs || !SaysOurs(path))
      {
         throw new InvalidOperationException(NameTaken);
      }
      ClearReadOnly(path);
      // Windows reports the replacement of a file another program has open as access denied, whatever that program
      // shares, which says nothing about why. Opening the file with nothing shared first reports it as what it is,
      // a sharing violation, so that it is tried again like any other file in use and noted as one.
      File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None).Dispose();
      File.Move(temporary, path, overwrite: true);
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



   /// <summary>What a first look at an existing file under a shortcut name found.</summary>
   private enum Whose
   {
      /// <summary>A shortcut of ours.</summary>
      Ours,

      /// <summary>Someone else's shortcut, a file that holds no shortcut at all, or a link.</summary>
      NotOurs,

      /// <summary>The file could not be opened, so nothing is known about it: it may be ours.</summary>
      NotOpened,
   }



   /// <summary>
   /// The first look of a branding run at an existing file under a shortcut name, before anything is changed. A file
   /// that cannot be opened is not judged: nothing is known about it, the kind of the failure is given, and the caller
   /// decides what that means. Asked to be patient, the look gives a file in use the tries of
   /// <see cref="WhileHeld"/> first. A folder that has a link on the way to it, and a shell that cannot do its work,
   /// are not about this file and fail as they always did.
   /// </summary>
   private Whose Judge(string path, bool patiently, out string kind)
   {
      kind = "";
      MsiUpdateInstaller.ValidateNoReparseComponents(Path.GetDirectoryName(path)!);
      bool ours = false;
      try
      {
         if(patiently)
         {
            WhileHeld(() => ours = SaysOurs(path));
         }
         else
         {
            ours = SaysOurs(path);
         }
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
      {
         kind = Kind(exception);
         return Whose.NotOpened;
      }
      return ours ? Whose.Ours : Whose.NotOurs;
   }



   /// <summary>
   /// The first look at a file that already has the name the new shortcut needs, and what it means for the run. A
   /// shortcut of ours is to be replaced, also one that nothing records: that is how an install takes over a shortcut
   /// an uninstall had to leave behind. A file that is not ours refuses the run. A file that cannot be opened, after
   /// the tries a file in use is given, is not judged and therefore never overwritten. When the record names it, it
   /// is the shortcut an earlier run of ours made, which starts the same companion whatever version wrote it: the run
   /// goes on without replacing it, with a note, and answers false. When nothing records it, nothing says it is
   /// ours, and the run is refused with what the operator can do.
   /// </summary>
   private bool OursToReplace(string destination, bool recorded, List<string> notes)
   {
      string filename = Path.GetFileName(destination);
      switch(Judge(destination, patiently: true, out string kind))
      {
         case Whose.Ours:
            return true;
         case Whose.NotOurs:
            throw new InvalidOperationException(NameTaken);
         default:
            if(!recorded)
            {
               throw new IOException(NotJudged(filename, kind));
            }
            notes.Add(KeptAsItIs(filename, "opened", kind));
            return false;
      }
   }



   /// <summary>
   /// One look at an existing file under a shortcut name: whether it is a shortcut of ours. A link is not ours and is
   /// not opened, because this product never writes one. A file that opens and holds no shortcut at all is not ours,
   /// and neither is a shortcut that says anything but what ours says. A file that cannot be opened fails as the file
   /// system fails, with Windows' own result code, which is how a file in use is told from one that is denied.
   /// </summary>
   private bool SaysOurs(string path)
      => !security.IsLink(path) && shell.Read(path) is { } link && MatchesOwnedShortcut(link.Target, link.Arguments, link.Description);



   /// <summary>
   /// Clears the read-only mark of a file that has just been found to be a shortcut of ours, so that it can be replaced
   /// or deleted, or of a temporary file an earlier run left behind, which is ours by its name. It is never called for
   /// a file that is not ours.
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



   /// <summary>
   /// Whether an uninstall may work in the shortcut folder. A folder that can no longer be trusted (a link on the way
   /// to it, an untrusted owner, or write access for an untrusted account), or whose security cannot be read, is not
   /// worked in: nothing in it is opened, changed or deleted, because an elevated process must not act on files an
   /// ordinary user could have put there. That does not fail the uninstall, which would only keep the product
   /// installed and protect nothing more; one note says that shortcuts of ours may remain there.
   /// </summary>
   private bool TrustedForRemoval(List<string> notes)
   {
      try
      {
         ValidateShortcutDirectory(ShortcutDirectory);
         return true;
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
      {
         notes.Add($"The shortcut folder can no longer be trusted, or its security could not be read ({exception.GetType().Name}), so nothing in it was opened or removed. Shortcuts of this application may remain in it; they are no longer recorded.");
         return false;
      }
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
