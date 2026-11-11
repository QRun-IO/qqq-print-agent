/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>Arguments of <see cref="ManagementOperations.PrintersTestPrint"/>: the shared printer, the document format to test, and optionally which of the server's test documents to use.</summary>
public sealed record PrintersTestPrintArgs(string PrinterId, string Format, string? TestProfile = null);
