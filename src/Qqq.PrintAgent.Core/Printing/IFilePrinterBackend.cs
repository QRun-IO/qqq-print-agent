/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>Consumes a verified file while its owner retains the read-only lease; no document travels in a control message.</summary>
public interface IFilePrinterBackend : IPrinterBackend
{
   Task<string> PrintFileAsync(PrintJob job, string path, CancellationToken cancellationToken);
   Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken)
      => PrintFileAsync(job, path, cancellationToken);
}
