/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.AccessControl;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The Windows facts about who owns a path and who may change it, and the one action that depends on them: creating a
/// folder that is protected from the moment it exists. An implementation reads and creates; whether an owner or an access
/// list is acceptable, and what access a new folder gets, is decided by the caller.
/// </summary>
internal interface IPathSecurity
{
   /// <summary>True when the current process holds administrator rights.</summary>
   bool IsAdministrator { get; }



   /// <summary>The security identifier of the user the current process runs as, or null for an identity that has none.</summary>
   string? CurrentUserSid { get; }



   /// <summary>The owner and access list of an existing folder.</summary>
   FileSystemSecurity ReadDirectorySecurity(string directory);



   /// <summary>Creates a folder that has the given owner and access list from the moment it exists.</summary>
   void CreateDirectory(string directory, DirectorySecurity security);
}