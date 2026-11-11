/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The result of <see cref="ManagementOperations.JobsCancel"/>: where the job was cancelled. In the Windows spooler, pages
/// already sent to the printer may still print, so the operator is told to look at the output; on the server, the job had
/// not been dispatched and nothing prints. A cancellation Windows refuses is not a result but
/// <see cref="ManagementErrorCode.CancelRefused"/>. A client treats a place it does not know as the spooler, the case
/// that asks the operator to look.
/// </summary>
public sealed record JobCancelResult(string CancelledAt)
{
   /// <summary>Cancellation was requested from Windows for a document already in the spooler.</summary>
   public const string Spooler = "spooler";



   /// <summary>The server accepted the cancellation before the job was dispatched.</summary>
   public const string Server = "server";
}
