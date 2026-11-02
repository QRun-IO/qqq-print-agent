/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>A job as the server describes it. The content length and SHA-256 let the agent verify the downloaded document before anything reaches a printer, so a truncated or substituted file is rejected.</summary>
public sealed record PrintJob(string JobId, string PrinterId, string Title, string Format, string ContentSha256, long ContentLength, PrintOptions Options, string CreatedAt, string Status);


