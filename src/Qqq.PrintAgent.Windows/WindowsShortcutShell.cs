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
/// It is not excluded from coverage: a test writes and reads a real shortcut file through it in a temporary folder, which
/// checks the property names it uses against Windows itself.
/// </summary>
internal sealed class WindowsShortcutShell : IShortcutShell
{
   /// <summary>Opens the shortcut and reads its program, arguments and description, treating a missing value as empty.</summary>
   public ShortcutLink Read(string path)
   {
      ShortcutLink? link = null;
      WithShortcut(path, shortcut => link = new ShortcutLink(GetProperty(shortcut, "TargetPath"), GetProperty(shortcut, "Arguments"), GetProperty(shortcut, "Description")));
      return link!;
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



   /// <summary>Calls a member of a late-bound COM object with the invariant culture, since shortcut automation has no compile-time type.</summary>
   private static object? Invoke(object instance, string name, BindingFlags flags, object[]? arguments)
      => instance.GetType().InvokeMember(name, flags, null, instance, arguments, CultureInfo.InvariantCulture);



   /// <summary>Reads a string property of a shortcut, treating a missing value as empty.</summary>
   private static string GetProperty(object instance, string name) => Invoke(instance, name, BindingFlags.GetProperty, null) as string ?? "";



   /// <summary>Sets a string property of a shortcut.</summary>
   private static void SetProperty(object instance, string name, string value) => Invoke(instance, name, BindingFlags.SetProperty, [value]);
}
