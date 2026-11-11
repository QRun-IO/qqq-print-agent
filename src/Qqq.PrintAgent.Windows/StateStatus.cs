/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Whether the service may use the state it has stored, as it found out when it started. It is one of three cases and
/// each carries only what belongs to it, so a caller cannot read a file name from a ready state or a version from an
/// interrupted upgrade. While the state is not ready the service runs no connection and answers only status, the log
/// operations and the update operations; <see cref="Refusal"/> is what it tells everything else.
/// </summary>
internal abstract record StateStatus
{
   /// <summary>
   /// What the service answers, in the one case of a newer build's data, to every operation it will not carry out, and
   /// shows as its status. A later step gives the refusal an error code; until then clients have this text, so it is kept
   /// in one place.
   /// </summary>
   internal const string NewerBuildText = "A newer version of this application wrote the print data on this computer, and this version cannot use it. Nothing was changed. Install the newer version again to continue printing.";



   /// <summary>What the service answers while an upgrade from 1.x could not finish. The snapshot names the file. The wording is provisional: the step that moves the files settles it.</summary>
   internal const string UpgradeIncompleteText = "The upgrade of the print data on this computer could not finish. Nothing was lost. Restart the print service to try again.";



   /// <summary>Closed to this file: the three cases below are the only ones.</summary>
   private StateStatus()
   {
   }



   /// <summary>The sentence the service answers refused operations with, or null when the state is ready and nothing is refused.</summary>
   internal abstract string? Refusal { get; }



   /// <summary>The same case in the form the management contract's snapshot carries.</summary>
   internal abstract StoredState ToStoredState();



   /// <summary>The stored state is current: the journal is at the schema version this build works with.</summary>
   internal sealed record Ready : StateStatus
   {
      /// <summary>Nothing is refused.</summary>
      internal override string? Refusal => null;



      /// <summary>Ready carries no detail.</summary>
      internal override StoredState ToStoredState() => new(StoredState.Ready);
   }



   /// <summary>The upgrade from 1.x stopped at a file it could not move; <paramref name="File"/> is that file, relative to the state folder. No step produces this yet: the moves arrive with the per-server folders.</summary>
   internal sealed record UpgradeIncomplete(string File) : StateStatus
   {
      /// <summary>The upgrade sentence.</summary>
      internal override string? Refusal => UpgradeIncompleteText;



      /// <summary>Carries the file the upgrade stopped at.</summary>
      internal override StoredState ToStoredState() => new(StoredState.UpgradeIncomplete, File: File);
   }



   /// <summary>A newer build wrote the journal: it is at schema version <paramref name="FoundVersion"/> and this build knows versions up to <paramref name="SupportedVersion"/>. The journal was only read.</summary>
   internal sealed record NewerBuild(int FoundVersion, int SupportedVersion) : StateStatus
   {
      /// <summary>The newer-build sentence.</summary>
      internal override string? Refusal => NewerBuildText;



      /// <summary>Carries the version found and the newest this build knows.</summary>
      internal override StoredState ToStoredState() => new(StoredState.NewerBuild, FoundVersion: FoundVersion, SupportedVersion: SupportedVersion);
   }
}
