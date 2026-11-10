/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>Filters cloud inventory and rejects jobs for queues no longer exposed locally.</summary>
public sealed class ExposedPrinterBackend(IPrinterBackend inner, Func<IReadOnlySet<string>> exposedIds) : IPrinterBackend
{
   /// <summary>Returns only printers the operator chose to share and that have at least one usable format, so nothing else is ever offered to QQQ.</summary>
   public IReadOnlyList<PrinterInfo> Discover()
   {
      IReadOnlySet<string> exposed = exposedIds();
      return inner.Discover().Where(printer => exposed.Contains(printer.PrinterId) && printer.Formats.Length > 0).ToArray();
   }



   /// <summary>Prints a staged file only if the job's printer is still shared on this computer, rechecked at submission time because the operator may have unshared it since the job was claimed.</summary>
   public Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken)
   {
      if(!exposedIds().Contains(job.PrinterId))
      {
         throw new InvalidOperationException("The printer is not exposed to QQQ on this computer.");
      }
      return inner.PrintFileAsync(job, path, submissionTimeout, cancellationToken);
   }
}

