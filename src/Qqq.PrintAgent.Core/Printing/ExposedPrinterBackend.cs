/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>Filters cloud inventory and rejects jobs for queues no longer exposed locally.</summary>
public sealed class ExposedPrinterBackend(IPrinterBackend inner, Func<IReadOnlySet<string>> exposedIds) : IFilePrinterBackend
{
   public IReadOnlyList<PrinterInfo> Discover()
   {
      IReadOnlySet<string> exposed = exposedIds();
      return inner.Discover().Where(printer => exposed.Contains(printer.PrinterId) && printer.Formats.Length > 0).ToArray();
   }



   public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
   {
      if(!exposedIds().Contains(job.PrinterId))
      {
         throw new InvalidOperationException("The printer is not exposed to QQQ on this computer.");
      }
      return inner.PrintAsync(job, content, cancellationToken);
   }

   public Task<string> PrintFileAsync(PrintJob job, string path, CancellationToken cancellationToken)
      => PrintFileAsync(job, path, TimeSpan.FromMinutes(2), cancellationToken);

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

