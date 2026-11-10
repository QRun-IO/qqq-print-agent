/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// One Windows printer as this computer sees it: its stable identifier, the queue, driver and port, its status, the document
/// formats it can print unattended and its measured default media. Whether it is shared, and under what name, is not here:
/// that differs per server and is in each <see cref="ServerSnapshot"/>.
/// </summary>
public sealed record ComputerPrinter(string PrinterId, string Name, string DriverName, string PortName, string Status, IReadOnlyList<string> Formats,
   IReadOnlyList<PrinterMediaProfile>? MediaProfiles = null)
{
   /// <summary>Takes what describes the Windows queue from a discovered printer and leaves out its operator-given name, description and location, which belong to a server.</summary>
   public static ComputerPrinter From(PrinterInfo printer)
   {
      ArgumentNullException.ThrowIfNull(printer);
      return new ComputerPrinter(printer.PrinterId, printer.Name, printer.DriverName, printer.PortName, printer.Status, printer.Formats, printer.MediaProfiles);
   }
}
