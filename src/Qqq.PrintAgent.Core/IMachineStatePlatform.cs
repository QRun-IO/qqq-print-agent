/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.AccessControl;

namespace Qqq.PrintAgent.Core;

/// <summary>
/// The Windows facts the machine state rules are applied to: who the process runs as, what a path's attributes and
/// security descriptor are, and how a protected folder is created. An implementation only reads or creates; whether what
/// it returns is acceptable is decided by <see cref="MachineStateGuard"/>.
/// </summary>
internal interface IMachineStatePlatform
{
   /// <summary>True when the process runs with administrator rights, the only case in which the machine folder may be created.</summary>
   bool IsAdministrator { get; }



   /// <summary>The attributes of an existing file or folder; a link shows as a reparse point.</summary>
   FileAttributes GetAttributes(string path);



   /// <summary>The security descriptor of a folder: owner and access rules.</summary>
   FileSystemSecurity GetDirectorySecurity(string path);



   /// <summary>The security descriptor of a file: owner and access rules.</summary>
   FileSystemSecurity GetFileSecurity(string path);



   /// <summary>Creates a folder that has the given security descriptor from the moment it exists.</summary>
   void CreateProtectedDirectory(string path, string descriptor);
}
