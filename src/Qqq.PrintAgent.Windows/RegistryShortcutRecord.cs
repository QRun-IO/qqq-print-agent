/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The real Windows behind <see cref="IShortcutRecord"/>: two values under this product's key in the machine registry
/// hive. The current shortcut's name is the string value 1.x already wrote, so an upgrade finds it and a 1.x product
/// restored by a failed upgrade still reads it; the other names are a multi-string value beside it.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Opens, reads, writes and deletes two values under HKEY_LOCAL_MACHINE. A test must not change this computer's registry, and writing there needs administrator rights. No rule lives here: each member opens this product's key and makes one call on one value, the proof of changeability opens the key for writing and creates it when it is missing, and a missing key or value reads as nothing recorded. When the record is proved changeable, when each name is read, written or forgotten, and in which order, is decided in AgentBrandingShortcuts, which is tested with a fake of IShortcutRecord.")]
internal sealed class RegistryShortcutRecord : IShortcutRecord
{
   private const string RegistryPath = @"SOFTWARE\QRunIO\PrintAgent";
   private const string LastName = "LastShortcutName";
   private const string PendingNames = "PendingShortcutNames";



   /// <summary>Opens the product key for writing, creating it when it is missing, and closes it again; Windows refuses when the caller may not write there.</summary>
   public void RequireChangeable() => Registry.LocalMachine.CreateSubKey(RegistryPath).Dispose();



   /// <summary>Reads the string value through a read-only handle.</summary>
   public string? ReadLastShortcutName()
   {
      using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryPath);
      return key?.GetValue(LastName) as string;
   }



   /// <summary>Reads the multi-string value through a read-only handle.</summary>
   public IReadOnlyList<string> ReadPendingShortcutNames()
   {
      using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryPath);
      return key?.GetValue(PendingNames) as string[] ?? [];
   }



   /// <summary>Stores the name as a string value.</summary>
   public void WriteLastShortcutName(string filename)
   {
      using RegistryKey key = Registry.LocalMachine.CreateSubKey(RegistryPath);
      key.SetValue(LastName, filename, RegistryValueKind.String);
   }



   /// <summary>Stores the names as a multi-string value.</summary>
   public void WritePendingShortcutNames(IReadOnlyList<string> filenames)
   {
      using RegistryKey key = Registry.LocalMachine.CreateSubKey(RegistryPath);
      key.SetValue(PendingNames, filenames.ToArray(), RegistryValueKind.MultiString);
   }



   /// <summary>Deletes the string value when the key and the value exist.</summary>
   public void ClearLastShortcutName()
   {
      using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryPath, writable: true);
      key?.DeleteValue(LastName, throwOnMissingValue: false);
   }



   /// <summary>Deletes the multi-string value when the key and the value exist.</summary>
   public void ClearPendingShortcutNames()
   {
      using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryPath, writable: true);
      key?.DeleteValue(PendingNames, throwOnMissingValue: false);
   }
}
