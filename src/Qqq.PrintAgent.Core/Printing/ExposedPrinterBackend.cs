/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// Filters cloud inventory and rejects jobs for queues no longer exposed locally. One rule decides what each of the three reads
/// lets through: a printer is offered only when the operator shared it and it has at least one usable format.
/// </summary>
public sealed class ExposedPrinterBackend(IPrinterBackend inner, Func<IReadOnlySet<string>> exposedIds) : IPrinterBackend
{
   /// <summary>Returns only printers the operator chose to share and that have at least one usable format, so nothing else is ever offered to QQQ. The inner backend reads the settings of every printer for it.</summary>
   public IReadOnlyList<PrinterInfo> Discover()
   {
      IReadOnlySet<string> exposed = exposedIds();
      return inner.Discover().Where(printer => IsOffered(exposed, printer)).ToArray();
   }



   /// <summary>Returns the same printers as <see cref="Discover"/>, by the same rule, without their media profiles; the inner backend reads the settings of no printer for it.</summary>
   public IReadOnlyList<PrinterInfo> ListWithoutMedia()
   {
      IReadOnlySet<string> exposed = exposedIds();
      return inner.ListWithoutMedia().Where(printer => IsOffered(exposed, printer)).ToArray();
   }



   /// <summary>
   /// Returns the printer only when <see cref="Discover"/> would list it: one that is not shared, or that has no usable format,
   /// reads as absent. A printer that is not shared is never asked about, so the inner backend reads the settings of the one
   /// shared printer and of nothing else.
   /// </summary>
   public PrinterInfo? FindWithMedia(string printerId)
   {
      IReadOnlySet<string> exposed = exposedIds();
      if(!exposed.Contains(printerId))
      {
         return null;
      }
      PrinterInfo? printer = inner.FindWithMedia(printerId);
      return printer is not null && IsOffered(exposed, printer) ? printer : null;
   }



   /// <summary>
   /// Prints a staged file only if the job's printer is still shared on this computer, rechecked at submission time
   /// because the operator may have unshared it since the job was claimed. The refusal is made before the backend
   /// behind the filter is called and has its own type, <see cref="PrinterNotExposedException"/>, so the caller knows
   /// that nothing was submitted.
   /// </summary>
   public Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken)
   {
      if(!exposedIds().Contains(job.PrinterId))
      {
         throw new PrinterNotExposedException("The printer is not exposed to QQQ on this computer; no bytes were submitted.");
      }
      return inner.PrintFileAsync(job, path, submissionTimeout, cancellationToken);
   }



   /// <summary>The one rule behind all three reads: the operator shared the printer, and it has at least one format it can print unattended.</summary>
   private static bool IsOffered(IReadOnlySet<string> exposed, PrinterInfo printer) => exposed.Contains(printer.PrinterId) && printer.Formats.Length > 0;
}
