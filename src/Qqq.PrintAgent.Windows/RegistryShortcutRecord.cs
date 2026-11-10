/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32;

namespace Qqq.PrintAgent.Windows;

/// <summary>The real Windows behind <see cref="IShortcutRecord"/>: one string value under this product's key in the machine registry hive.</summary>
[ExcludeFromCodeCoverage(Justification = "Reads and writes a value under HKEY_LOCAL_MACHINE. A test must not change this computer's registry, and writing there needs administrator rights. No rule lives here: it stores, returns and deletes one string. When the name is read, replaced or forgotten is decided in AgentBrandingShortcuts, which is tested with a fake of IShortcutRecord.")]
internal sealed class RegistryShortcutRecord : IShortcutRecord
{
   private const string RegistryPath = @"SOFTWARE\QRunIO\PrintAgent";
   private const string ValueName = "LastShortcutName";



   /// <summary>Reads the value, creating the product key first when asked to.</summary>
   public string? ReadLastShortcutName(bool createStore)
   {
      using RegistryKey? key = createStore ? Registry.LocalMachine.CreateSubKey(RegistryPath) : Registry.LocalMachine.OpenSubKey(RegistryPath, writable: true);
      return key?.GetValue(ValueName) as string;
   }



   /// <summary>Stores the value as a string.</summary>
   public void WriteLastShortcutName(string filename)
   {
      using RegistryKey key = Registry.LocalMachine.CreateSubKey(RegistryPath);
      key.SetValue(ValueName, filename, RegistryValueKind.String);
   }



   /// <summary>Deletes the value when the key and the value exist.</summary>
   public void ClearLastShortcutName()
   {
      using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryPath, writable: true);
      key?.DeleteValue(ValueName, throwOnMissingValue: false);
   }
}