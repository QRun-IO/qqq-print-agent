/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Printers;

/// <summary>One entry of a test-print option box: the text the operator reads and the value sent to the service when it is chosen.</summary>
public sealed record TestPrintChoice(string Label, string Value);
