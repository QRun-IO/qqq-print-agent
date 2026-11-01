/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// What the worker needs from a printing platform: list the printers it can use and submit one document. Windows, the isolated helper,
/// the exposure filter and test fakes all implement it, so the worker's safety rules are written once.
/// </summary>
public interface IPrinterBackend
{
   /// <summary>Lists the printers this backend can print to, with their formats, status and media profiles.</summary>
   IReadOnlyList<PrinterInfo> Discover();



   /// <summary>Submits one document to the job's printer and returns the spooler receipt; a failure after submission began means the outcome is uncertain.</summary>
   Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken);
}




