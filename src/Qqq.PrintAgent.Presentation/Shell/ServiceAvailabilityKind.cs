/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>What the last read of the snapshot says about the local service.</summary>
public enum ServiceAvailabilityKind
{
   /// <summary>No read has been answered or has failed yet.</summary>
   Checking,

   /// <summary>The service answered and its stored data is ready.</summary>
   Reached,

   /// <summary>The service answered, its stored data is ready, and claiming is paused while an update is installed.</summary>
   UpdatePaused,

   /// <summary>The service answered; the upgrade from 1.x stopped at a file, so it runs no connections.</summary>
   UpgradeIncomplete,

   /// <summary>The service answered; a newer build wrote its stored data, so it runs no connections.</summary>
   NewerBuild,

   /// <summary>The service answered; its stored data is not ready, in a way this version does not know.</summary>
   StoredDataNotReady,

   /// <summary>The service could not be reached, or did not answer in time.</summary>
   NotReached,

   /// <summary>A service that speaks an older version of the pipe answered.</summary>
   OlderService,

   /// <summary>A service that speaks a newer version of the pipe answered.</summary>
   NewerService,

   /// <summary>A reply arrived and could not be read.</summary>
   Unreadable,

   /// <summary>The service refused to give a snapshot.</summary>
   Refused,
}
