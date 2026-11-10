/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Qqq.PrintAgent.Core;

/// <summary>Reads the labels the installer persisted for this machine, so an unattended upgrade keeps a deployment's identity.</summary>
[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage(Justification = "A single read of the machine registry hive, which a test must not depend on or write to. Whether a label is used is decided in RuntimeBranding.LoadMachine, tested with a supplied reader.")]
internal static class MachineInstallerLabels
{
   /// <summary>Returns the named label from the installer's machine key, or null when the key or the value is absent or is not text.</summary>
   internal static string? Read(string name)
   {
      using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\QRunIO\PrintAgent");
      return key?.GetValue(name) as string;
   }
}
