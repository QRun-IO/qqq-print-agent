/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// What reading the saved session did about a pairing change that was staged and not finished. The store has no log of
/// its own, so it reports this and the runtime writes the line: moving, discarding or setting aside a credential file
/// should never happen silently.
/// </summary>
internal enum StagedPairingOutcome
{
   /// <summary>Nothing was staged, or a leftover could not be removed yet and waits for the next read.</summary>
   None,

   /// <summary>The staged session belonged to the stored pairing and was moved into place.</summary>
   Completed,

   /// <summary>As <see cref="Completed"/>, after an unreadable earlier session was moved aside so it was not overwritten.</summary>
   CompletedAfterQuarantine,

   /// <summary>The staged session belongs to the stored pairing and is being used, but could not be moved into place yet; the next reader tries again.</summary>
   Deferred,

   /// <summary>The staged session did not belong to the stored pairing, or the stored session already did or was saved after it, and it was deleted.</summary>
   Discarded,

   /// <summary>The staged session could not be read and was moved aside under a recovery name instead of being deleted.</summary>
   SetAside,
}
