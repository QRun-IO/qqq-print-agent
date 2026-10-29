/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

public interface IPrinterBackend
{
   IReadOnlyList<PrinterInfo> Discover();
   Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken);
}




