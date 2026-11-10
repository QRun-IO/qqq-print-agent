/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The outcome of resolving a <c>--server</c> value: the server that was selected, or why none was, with the identifiers
/// of the servers the value could mean so the refusal can list them.
/// </summary>
public sealed record ServerSelection(ServerSelectionOutcome Outcome, string? ServerId, IReadOnlyList<string> Candidates)
{
   /// <summary>
   /// The error code the command line reports for this outcome, or null for a selection. An ambiguous value is
   /// <see cref="ManagementErrorCode.ServerAmbiguous"/>; a value that matches nothing, or having no servers at all, is
   /// <see cref="ManagementErrorCode.ServerNotFound"/>; a missing value where one is needed and a prefix that is too
   /// short are malformed requests.
   /// </summary>
   public string? ErrorCode() => Outcome switch
   {
      ServerSelectionOutcome.Selected => null,
      ServerSelectionOutcome.Ambiguous => ManagementErrorCode.ServerAmbiguous,
      ServerSelectionOutcome.NoServers or ServerSelectionOutcome.NotFound => ManagementErrorCode.ServerNotFound,
      _ => ManagementErrorCode.InvalidRequest,
   };
}
