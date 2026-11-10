/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The outcome of reading one reply's text: the reply, or what was wrong with its envelope. A client needs the
/// difference: text that is not JSON, or has no version, came from a service that speaks version 1; an unknown version
/// came from a newer one; and either is something to tell the operator, not a failed operation.
/// </summary>
public sealed record ManagementReplyParse(ManagementReply? Reply, ManagementEnvelopeProblem Problem)
{
   /// <summary>True when the envelope was read and <see cref="Reply"/> is set.</summary>
   [MemberNotNullWhen(true, nameof(Reply))]
   public bool Succeeded() => Reply is not null;
}
