/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Printers;

/// <summary>
/// The read-only fields shown when a printer row is expanded: the Windows queue name, the location and description (with
/// "Not set" for a blank, so an empty value is never mistaken for a missing control) and the driver, port and identifier
/// an administrator needs for diagnosis.
/// </summary>
public sealed record PrinterDetails(string Queue, string Location, string Description, string Technical)
{
   /// <summary>Builds the expanded fields for a printer.</summary>
   public static PrinterDetails From(PrinterSnapshot printer)
   {
      ArgumentNullException.ThrowIfNull(printer);
      return new PrinterDetails(printer.Name,
         string.IsNullOrWhiteSpace(printer.Location) ? "Not set" : printer.Location,
         string.IsNullOrWhiteSpace(printer.Description) ? "Not set" : printer.Description,
         $"Driver: {printer.DriverName}\nPort: {printer.PortName}\nPrinter ID: {printer.PrinterId}");
   }
}
