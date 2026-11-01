/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>A media validation refusal made before StartDoc/StartDocPrinter; no document bytes were submitted.</summary>
public sealed class PrinterMediaMismatchException(string message) : InvalidOperationException(message);
