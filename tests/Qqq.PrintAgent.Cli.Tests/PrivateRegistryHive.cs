/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// A registry hive of the test's own: one file in a temporary folder the test creates, loaded with RegLoadAppKey so that
/// only this process sees it. It is no part of the machine hive or of any user's hive, needs no administrator rights,
/// and is unloaded and deleted with its folder when the test is done. Code that takes a base key can be run against it
/// exactly as against the real registry, while nothing on this computer's registry is read or changed.
/// </summary>
internal sealed class PrivateRegistryHive : IDisposable
{
   /// <summary>The access asked for on the hive's root: everything a key allows.</summary>
   private const int KeyAllAccess = 0xF003F;



   /// <summary>The temporary folder that holds the hive file and the log files Windows keeps beside it.</summary>
   private readonly string _folder = Path.Combine(Path.GetTempPath(), "qqq-private-hive-" + Guid.NewGuid().ToString("N"));



   /// <summary>The root key of the private hive, in the 64-bit view like the record of the installed product.</summary>
   internal RegistryKey Root { get; }



   /// <summary>Creates the folder and loads a new, empty hive from a file in it.</summary>
   internal PrivateRegistryHive()
   {
      Directory.CreateDirectory(_folder);
      int result = RegLoadAppKeyW(Path.Combine(_folder, "test.hive"), out SafeRegistryHandle handle, KeyAllAccess, 0, 0);
      if(result != 0)
      {
         Directory.Delete(_folder, recursive: true);
         throw new Win32Exception(result, "A private registry hive could not be loaded from a file in the test's temporary folder (RegLoadAppKey).");
      }
      Root = RegistryKey.FromHandle(handle, RegistryView.Registry64);
   }



   /// <summary>Closes the root, which unloads the hive, and removes the folder with the hive file.</summary>
   public void Dispose()
   {
      Root.Dispose();
      Directory.Delete(_folder, recursive: true);
   }



   /// <summary>Loads a hive file as a private hive of the calling process and returns a handle to its root key.</summary>
   [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
   [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
   private static extern int RegLoadAppKeyW(string file, out SafeRegistryHandle result, int samDesired, int options, int reserved);
}
