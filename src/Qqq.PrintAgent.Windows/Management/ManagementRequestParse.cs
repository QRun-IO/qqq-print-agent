/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The outcome of reading one request's text: the request, or what was wrong with its envelope. It is a value, not an
/// exception, because a malformed request is an ordinary event on a pipe any administrator's program can write to, and
/// the dispatcher answers each problem differently.
/// </summary>
public sealed record ManagementRequestParse(ManagementRequest? Request, ManagementEnvelopeProblem Problem)
{
   /// <summary>True when the envelope was read and <see cref="Request"/> is set.</summary>
   [MemberNotNullWhen(true, nameof(Request))]
   public bool Succeeded() => Request is not null;



   /// <summary>True when the request has no version, which is how a version 1 client is recognized; it is answered in the version 1 format, not with a version 2 reply.</summary>
   public bool IsFromVersionOneClient() => Problem == ManagementEnvelopeProblem.VersionMissing;



   /// <summary>The code a version 2 reply carries for this problem, or null when there is none: an unknown or missing version is <see cref="ManagementErrorCode.UnsupportedVersion"/>, every other problem <see cref="ManagementErrorCode.InvalidRequest"/>.</summary>
   public string? ErrorCode() => Problem switch
   {
      ManagementEnvelopeProblem.None => null,
      ManagementEnvelopeProblem.VersionMissing or ManagementEnvelopeProblem.VersionUnsupported => ManagementErrorCode.UnsupportedVersion,
      _ => ManagementErrorCode.InvalidRequest,
   };
}
