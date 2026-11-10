/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Printers;

/// <summary>
/// Everything one printer row shows, worked out from the service's snapshot: the five cell texts, their tooltips, the
/// sentence behind the server-approval word and the accessible name of the share checkbox. The row view only copies
/// these into controls, so the wording and the fallbacks are decided here where a test can read them.
/// </summary>
public sealed record PrinterRow(string Name, string Status, string Server, string Location, string Formats,
   string NameToolTip, string StatusToolTip, string ServerDetail, string ShareName)
{
   /// <summary>Builds the row for a printer, given the service's last reading of the server catalog and what this edition calls the server.</summary>
   public static PrinterRow From(PrinterSnapshot printer, PrinterServerSync? sync, string serverName)
   {
      ArgumentNullException.ThrowIfNull(printer);
      string name = DisplayName(printer);
      return new PrinterRow(name, StatusLabel(printer), ServerLabel(printer),
         string.IsNullOrWhiteSpace(printer.Location) ? "—" : printer.Location,
         printer.Formats.Length == 0 ? "—" : string.Join(", ", printer.Formats),
         $"{name}\nWindows queue: {printer.Name}",
         printer.Formats.Length == 0 ? "Unavailable for unattended service printing. Select this printer for details." : JobLabels.Status(printer.Status),
         ServerDetailText(printer, sync, serverName),
         $"Share {name} with the server");
   }



   /// <summary>The name to show for a printer: the operator's friendly name when set, otherwise the Windows queue name.</summary>
   public static string DisplayName(PrinterSnapshot printer)
   {
      ArgumentNullException.ThrowIfNull(printer);
      return string.IsNullOrWhiteSpace(printer.DisplayName) ? printer.Name : printer.DisplayName;
   }



   /// <summary>
   /// The status word shown for a printer. A printer with no usable format cannot print unattended, so it reads "Offline" when
   /// the spooler says so and otherwise "Interactive only" instead of a misleading ready state.
   /// </summary>
   public static string StatusLabel(PrinterSnapshot printer)
   {
      ArgumentNullException.ThrowIfNull(printer);
      return printer.Formats.Length > 0 ? JobLabels.Status(printer.Status)
         : string.Equals(printer.Status, "OFFLINE", StringComparison.Ordinal) ? "Offline" : "Interactive only";
   }



   /// <summary>The server-approval word shown for a printer, from the service's last catalog reading for the connected server.</summary>
   public static string ServerLabel(PrinterSnapshot printer)
   {
      ArgumentNullException.ThrowIfNull(printer);
      return printer.ServerState switch
      {
         PrinterServerState.Approved => "Approved",
         PrinterServerState.AwaitingApproval => "Awaiting approval",
         PrinterServerState.NotReported => "Not reported",
         PrinterServerState.NotShared => "Not shared",
         _ => "Not checked",
      };
   }



   /// <summary>
   /// The sentence behind the server-approval word: what it means and, for a reading that failed, the service's reason.
   /// The server is named so a printer shared before a server change is never read as approved by the new one.
   /// </summary>
   public static string ServerDetailText(PrinterSnapshot printer, PrinterServerSync? sync, string serverName)
   {
      ArgumentNullException.ThrowIfNull(printer);
      string server = string.IsNullOrEmpty(sync?.Server) ? serverName : $"{serverName} ({sync.Server})";
      string freshness = string.Equals(sync?.Source, PrinterServerSync.RealtimeSource, StringComparison.Ordinal) ? " The server pushes a change the moment an administrator makes it." : " Checked about once a minute while connected.";
      return printer.ServerState switch
      {
         PrinterServerState.Approved => $"{server} approved this printer for this computer; it can receive jobs.{freshness}",
         PrinterServerState.AwaitingApproval => $"{server} received this printer in the inventory but has not approved it. An administrator approves it on the server (Printing → Print devices).{freshness}",
         PrinterServerState.NotReported => $"{server} did not report approval: {sync?.Detail ?? "the catalog could not be read."} Approve or check the printer on the server.",
         PrinterServerState.NotShared => "Not shared with the server. Turn on Shared to offer it; the server then has to approve it.",
         _ => $"Not checked yet with {server}. The service asks the server after it connects and sends the inventory.",
      };
   }
}
