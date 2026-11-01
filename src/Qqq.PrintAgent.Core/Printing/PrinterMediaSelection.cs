/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>An immutable reference to the exact queue default selected when the server created the job.</summary>
public sealed record PrinterMediaSelection(string Id, string Revision);
