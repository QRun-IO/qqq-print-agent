/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

public sealed record PrintJob(string JobId, string PrinterId, string Title, string Format, string ContentSha256, long ContentLength, PrintOptions Options, string CreatedAt, string Status);


