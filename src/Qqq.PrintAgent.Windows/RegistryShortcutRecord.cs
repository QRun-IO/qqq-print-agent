/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.Win32;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The registry behind <see cref="IShortcutRecord"/>: two values under one key. The current shortcut's name is the
/// string value 1.x already wrote, so an upgrade finds it and a 1.x product restored by a failed upgrade still reads it;
/// the other names are a multi-string value beside it. The base key and the key's path are given: the installed product
/// takes <see cref="Machine"/>, and a test gives it a private hive file in a temporary folder, so every member runs
/// under test and nothing here is excluded from coverage.
/// <para>
/// A value of a kind this record does not write is never used and never passed over silently: it is read as nothing
/// recorded and a warning is given. An expandable string is read as it is stored, with no environment name expanded,
/// because a file name is not a place to run an expansion.
/// </para>
/// </summary>
internal sealed class RegistryShortcutRecord(RegistryKey root, string path, Action<string> warn) : IShortcutRecord
{
   /// <summary>The key of the installed product under the machine hive.</summary>
   internal const string MachinePath = @"SOFTWARE\QRunIO\PrintAgent";



   /// <summary>The string value that names the current shortcut; 1.x wrote the same value.</summary>
   internal const string LastName = "LastShortcutName";



   /// <summary>The multi-string value that names the other shortcuts.</summary>
   internal const string PendingNames = "PendingShortcutNames";



   /// <summary>
   /// The record of the installed product: this product's key in the machine hive, always in the 64-bit view, so a
   /// 32-bit caller could never be redirected to another key. Only where a warning goes is given.
   /// </summary>
   internal static RegistryShortcutRecord Machine(Action<string> warn) => new(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), MachinePath, warn);



   /// <summary>The base key the record lives under.</summary>
   internal RegistryKey Root => root;



   /// <summary>The path of the record's key under the base key.</summary>
   internal string KeyPath => path;



   /// <summary>Opens the key for writing, creating it when it is missing, and closes it again; Windows refuses when the caller may not write there.</summary>
   public void RequireChangeable() => root.CreateSubKey(path).Dispose();



   /// <summary>Reads the name of the current shortcut through a read-only handle; a missing key or value is nothing recorded, and so, with a warning, is a value that is not a string.</summary>
   public string? ReadLastShortcutName()
   {
      object? value = Read(LastName);
      if(value is null or string)
      {
         return (string?)value;
      }
      warn($"The registry value {LastName} is not a string, so it was not used: no current shortcut is taken as recorded.");
      return null;
   }



   /// <summary>Reads the other names through a read-only handle; a missing key or value is nothing recorded, and so, with a warning, is a value that is not a list of strings.</summary>
   public IReadOnlyList<string> ReadPendingShortcutNames()
   {
      object? value = Read(PendingNames);
      if(value is null or string[])
      {
         return (string[]?)value ?? [];
      }
      warn($"The registry value {PendingNames} is not a list of strings, so it was not used: no other shortcuts are taken as recorded.");
      return [];
   }



   /// <summary>Stores the name as a string value, replacing a value of any kind.</summary>
   public void WriteLastShortcutName(string filename)
   {
      using RegistryKey key = root.CreateSubKey(path);
      key.SetValue(LastName, filename, RegistryValueKind.String);
   }



   /// <summary>Stores the names as a multi-string value, replacing a value of any kind.</summary>
   public void WritePendingShortcutNames(IReadOnlyList<string> filenames)
   {
      using RegistryKey key = root.CreateSubKey(path);
      key.SetValue(PendingNames, filenames.ToArray(), RegistryValueKind.MultiString);
   }



   /// <summary>Deletes the value that names the current shortcut when the key and the value exist.</summary>
   public void ClearLastShortcutName() => Delete(LastName);



   /// <summary>Deletes the value that names the other shortcuts when the key and the value exist.</summary>
   public void ClearPendingShortcutNames() => Delete(PendingNames);



   /// <summary>The value as it is stored, or null when the key or the value is missing. Nothing is created and no environment name is expanded.</summary>
   private object? Read(string name)
   {
      using RegistryKey? key = root.OpenSubKey(path);
      return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
   }



   /// <summary>Deletes one value; a missing key or value needs nothing.</summary>
   private void Delete(string name)
   {
      using RegistryKey? key = root.OpenSubKey(path, writable: true);
      key?.DeleteValue(name, throwOnMissingValue: false);
   }
}
