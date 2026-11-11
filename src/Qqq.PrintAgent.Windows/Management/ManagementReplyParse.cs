/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The outcome of reading one reply's text: the reply, or what was wrong with its envelope. A client needs the
/// difference: text that is not JSON is what a service that speaks version 1 answers; an unknown version is what a
/// newer one answers; and either tells the operator which side to update. Neither proves that the operation was not
/// carried out, because the same text can come from elsewhere, so a client says "nothing to check" only for a read.
/// JSON without a version has no known sender (a version 1 service does not answer in JSON) and is a reply that could
/// not be read.
/// </summary>
public sealed record ManagementReplyParse(ManagementReply? Reply, ManagementEnvelopeProblem Problem)
{
   /// <summary>True when the envelope was read and <see cref="Reply"/> is set.</summary>
   [MemberNotNullWhen(true, nameof(Reply))]
   public bool Succeeded() => Reply is not null;
}
