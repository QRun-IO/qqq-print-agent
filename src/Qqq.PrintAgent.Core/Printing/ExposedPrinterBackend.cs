/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>Filters cloud inventory and rejects jobs for queues no longer exposed locally.</summary>
public sealed class ExposedPrinterBackend(IPrinterBackend inner, Func<IReadOnlySet<string>> exposedIds) : IFilePrinterBackend
{
   /// <summary>Returns only printers the operator chose to share and that have at least one usable format, so nothing else is ever offered to QQQ.</summary>
   public IReadOnlyList<PrinterInfo> Discover()
   {
      IReadOnlySet<string> exposed = exposedIds();
      return inner.Discover().Where(printer => exposed.Contains(printer.PrinterId) && printer.Formats.Length > 0).ToArray();
   }



   /// <summary>Prints only if the job's printer is still shared on this computer, rechecked at submission time because the operator may have unshared it since the job was claimed.</summary>
   public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
   {
      if(!exposedIds().Contains(job.PrinterId))
      {
         throw new InvalidOperationException("The printer is not exposed to QQQ on this computer.");
      }
      return inner.PrintAsync(job, content, cancellationToken);
   }



   /// <summary>Prints a staged file with the default two minute native submission limit.</summary>
   public Task<string> PrintFileAsync(PrintJob job, string path, CancellationToken cancellationToken)
      => PrintFileAsync(job, path, TimeSpan.FromMinutes(2), cancellationToken);



   /// <summary>Prints a staged file only if the printer is still shared, and throws when the inner backend cannot take files, so neither condition is silently skipped.</summary>
   public Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken)
   {
      if(!exposedIds().Contains(job.PrinterId))
      {
         throw new InvalidOperationException("The printer is not exposed to QQQ on this computer.");
      }
      return inner is IFilePrinterBackend files ? files.PrintFileAsync(job, path, submissionTimeout, cancellationToken)
         : throw new NotSupportedException("The printer backend does not support file submissions.");
   }
}

