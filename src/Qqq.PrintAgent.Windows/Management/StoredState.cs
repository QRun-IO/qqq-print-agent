/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// Whether the service can use the data it has stored. When it cannot, it runs no connections and answers only the snapshot,
/// the log operations and the update operations; this record is how the snapshot says which case it is and why. For an
/// upgrade from 1.x that could not finish, <see cref="File"/> names the file it stopped at, relative to the state directory.
/// For data written by a newer build, the two versions are the schema version found and the newest this build knows. A
/// client treats a status it does not know as not ready.
/// </summary>
public sealed record StoredState(string Status, string? File = null, int? FoundVersion = null, int? SupportedVersion = null)
{
   /// <summary>The stored data is current and the service runs its connections.</summary>
   public const string Ready = "ready";



   /// <summary>The upgrade from 1.x could not move a file. Other operations are answered <see cref="ManagementErrorCode.MigrationIncomplete"/>; the next start tries again.</summary>
   public const string UpgradeIncomplete = "upgradeIncomplete";



   /// <summary>A newer build wrote the stored data. Other operations are answered <see cref="ManagementErrorCode.StateNewer"/> until that build is installed again; this build never migrates down.</summary>
   public const string NewerBuild = "newerBuild";
}
