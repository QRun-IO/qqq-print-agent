/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>Consumes a verified file while its owner retains the read-only lease; no document travels in a control message.</summary>
public interface IFilePrinterBackend : IPrinterBackend
{
   /// <summary>Prints the verified file at the path and returns the spooler receipt; the caller keeps its read-only lease until this returns, so the content cannot change underneath.</summary>
   Task<string> PrintFileAsync(PrintJob job, string path, CancellationToken cancellationToken);



   /// <summary>Same as the plain overload but with a bound on spooler submission; the default ignores the bound for backends that cannot enforce one, so older backends keep working.</summary>
   Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken)
      => PrintFileAsync(job, path, cancellationToken);
}
