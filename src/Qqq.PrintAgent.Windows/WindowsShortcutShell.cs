/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The real Windows behind <see cref="IShortcutShell"/>: the WScript.Shell automation object and the shortcut object it
/// creates. It copies values between a <see cref="ShortcutLink"/> and that object, and releases the COM objects afterwards.
/// The shortcut object does not fail on a file it cannot open or that is no shortcut: it answers with empty values, which
/// would read as somebody else's shortcut. So reading opens the file itself first, lets a failure to open it through,
/// and checks that the file begins as a shortcut file does before the shortcut object is asked anything.
/// It is not excluded from coverage: tests write and read real shortcut files through it in a temporary folder, which
/// checks the property names it uses against Windows itself.
/// </summary>
internal sealed class WindowsShortcutShell : IShortcutShell
{
   /// <summary>How every shortcut file begins: the size of its header (76) and the class identifier of a shell link, both fixed by the shell link file format.</summary>
   private static readonly byte[] ShortcutFileStart = [0x4C, 0x00, 0x00, 0x00, 0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46];



   /// <summary>
   /// Opens the file for reading, which fails as the file system fails when another program holds it or access is denied,
   /// and keeps it open, shared for reading only, while the shortcut is read, so the file that was checked is the file
   /// that is read. A file that does not begin as a shortcut file does (an empty file, or anything else under the name)
   /// is null. Otherwise it reads the shortcut's program, arguments and description, treating a missing value as empty.
   /// </summary>
   public ShortcutLink? Read(string path)
   {
      using FileStream held = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
      byte[] start = new byte[ShortcutFileStart.Length];
      if(held.ReadAtLeast(start, start.Length, throwOnEndOfStream: false) < start.Length || !start.AsSpan().SequenceEqual(ShortcutFileStart))
      {
         return null;
      }
      ShortcutLink? link = null;
      WithShortcut(path, shortcut => link = new ShortcutLink(GetProperty(shortcut, "TargetPath"), GetProperty(shortcut, "Arguments"), GetProperty(shortcut, "Description")));
      return link;
   }



   /// <summary>Creates the shortcut object for the path, sets its five values and saves it.</summary>
   public void Write(string path, ShortcutLink link)
      => WithShortcut(path, shortcut =>
      {
         SetProperty(shortcut, "TargetPath", link.Target);
         SetProperty(shortcut, "Arguments", link.Arguments);
         SetProperty(shortcut, "WorkingDirectory", link.WorkingDirectory);
         SetProperty(shortcut, "Description", link.Description);
         SetProperty(shortcut, "IconLocation", link.IconLocation);
         Invoke(shortcut, "Save", BindingFlags.InvokeMethod, null);
      });



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



   /// <summary>
   /// Calls a member of a late-bound COM object with the invariant culture, since shortcut automation has no compile-time
   /// type. A call the automation object refuses is reported as one kind of failure that names the member and carries
   /// what Windows said, so a caller can tell a shell that could not do its work from a file that could not be opened.
   /// </summary>
   private static object? Invoke(object instance, string name, BindingFlags flags, object[]? arguments)
   {
      try
      {
         return instance.GetType().InvokeMember(name, flags, null, instance, arguments, CultureInfo.InvariantCulture);
      }
      catch(Exception exception) when(exception is TargetInvocationException or COMException)
      {
         throw new InvalidOperationException($"Windows shortcut automation failed ({name}): {(exception.InnerException ?? exception).Message}", exception);
      }
   }



   /// <summary>Reads a string property of a shortcut, treating a missing value as empty.</summary>
   private static string GetProperty(object instance, string name) => Invoke(instance, name, BindingFlags.GetProperty, null) as string ?? "";



   /// <summary>Sets a string property of a shortcut.</summary>
   private static void SetProperty(object instance, string name, string value) => Invoke(instance, name, BindingFlags.SetProperty, [value]);
}
