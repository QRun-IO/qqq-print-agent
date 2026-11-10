/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The result of <see cref="ManagementOperations.ServersRemove"/>. Removal can outlast a pipe request, because a document
/// already handed to Windows is allowed to finish, so the reply says how far it got: started, with the service finishing it
/// by itself (and at its next start if it is stopped first), or finished.
/// </summary>
public sealed record ServerRemoveResult(string State)
{
   /// <summary>Removal has started: claiming has stopped and the server shows as being removed until the work in hand ends.</summary>
   public const string Removing = "removing";

   /// <summary>The server is gone from this computer.</summary>
   public const string Removed = "removed";
}
