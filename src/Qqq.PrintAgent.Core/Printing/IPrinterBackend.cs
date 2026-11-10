/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// What the worker needs from a printing platform: list the printers it can use and submit one staged document. Windows, the isolated
/// helper, the exposure filter, the profile wrapper and test fakes all implement it, so the worker's safety rules are written once. A
/// document reaches a backend only as a verified file, so no document travels in memory or in a control message, and every backend,
/// a wrapper around another included, can take one.
/// </summary>
public interface IPrinterBackend
{
   /// <summary>Lists the printers this backend can print to, with their formats, status and media profiles.</summary>
   IReadOnlyList<PrinterInfo> Discover();



   /// <summary>
   /// Prints the verified file at the path on the job's printer and returns the spooler receipt. The caller keeps its read-only
   /// lease on the file until this returns, so the content cannot change underneath. The limit bounds the native submission where
   /// the backend can enforce one. A failure after submission began, a timeout included, means the outcome is uncertain.
   /// </summary>
   Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken);
}




