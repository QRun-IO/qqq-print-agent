/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Runtime.Versioning;
using System.Security.AccessControl;

namespace Qqq.PrintAgent.Core;

/// <summary>
/// Rejects hostile pre-existing machine state before privileged consumers open it. This is the fixed entry point for the
/// real machine folder under ProgramData; the rules themselves live in <see cref="MachineStateGuard"/>, which a test can
/// point at a temporary folder.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MachineStateDirectory
{
   private static readonly MachineStateGuard Guard = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QqqPrintAgent"), new MachineStatePlatform());



   /// <summary>The machine state folder under ProgramData, the only location whose contents are protected and validated before privileged code opens them.</summary>
   public static string Root => Guard.Root;



   /// <summary>Private diagnostic paths remain independent; canonical machine state is always protected.</summary>
   public static void EnsureForPath(string path) => Guard.EnsureForPath(path);



   /// <summary>Validate existing branding without creating state merely to read optional settings.</summary>
   public static void ValidateExistingForPath(string path) => Guard.ValidateExistingForPath(path);



   /// <summary>Inspect every adopted descendant; never follow junctions or normalize hostile ownership.</summary>
   public static void ValidateTree(string directory) => Guard.ValidateTree(directory);



   /// <summary>Verify a newly created staging file before its privileged consumer opens it.</summary>
   public static void ValidateFile(string path) => Guard.ValidateFile(path);



   /// <summary>Requires an explicit access list, a trusted owner, and no allow rule for any account other than SYSTEM, Administrators or the Windows installer service.</summary>
   internal static void ValidateAcl(FileSystemSecurity security) => MachineStateGuard.ValidateAcl(security);
}
