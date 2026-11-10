/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The details of <see cref="ManagementErrorCode.CancelRefused"/>: why Windows did not cancel a document in the spooler.
/// Nothing in the Windows queue was changed. A client treats a reason it does not know as unknown, not as a failure to
/// read the reply.
/// </summary>
public sealed record CancelRefusedDetails(string Reason)
{
   /// <summary>The document is no longer in the Windows queue. Pages already printed cannot be recalled.</summary>
   public const string DocumentGone = "documentGone";



   /// <summary>The document in the queue could not be verified as the one this job spooled, so it was left alone.</summary>
   public const string OwnershipMismatch = "ownershipMismatch";
}
