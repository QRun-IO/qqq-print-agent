/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Printers;

/// <summary>The texts around the printer table: the one-line count above it and the hint shown when it is empty.</summary>
public static class PrintersSummary
{
   /// <summary>
   /// One line over the table: how many printers are shared and, for the connected server, how many of those it approved or still
   /// has to approve; when the server could not report approval, says so instead of counting.
   /// </summary>
   public static string Text(AgentSnapshot snapshot, string serverName)
   {
      ArgumentNullException.ThrowIfNull(snapshot);
      int shared = snapshot.Printers.Count(printer => printer.Exposed);
      string text = $"{snapshot.Printers.Count} printers · {shared} shared";
      if(shared == 0)
      {
         return text;
      }
      int awaiting = Count(snapshot, PrinterServerState.AwaitingApproval);
      return snapshot.PrinterSync?.Outcome switch
      {
         PrinterServerSync.Reported => text + $" · {Count(snapshot, PrinterServerState.Approved)} approved by {serverName}" + (awaiting > 0 ? $" · {awaiting} awaiting approval" : ""),
         PrinterServerSync.NotReported => text + $" · approval not reported by {serverName}",
         _ => text + $" · approval not checked with {serverName} yet",
      };
   }



   /// <summary>What an empty table tells the operator to do next: add a printer in Windows when the device is enrolled, and connect the device first when it is not.</summary>
   public static string EmptyText(bool enrolled)
      => enrolled ? "Add a printer in Windows Settings, then refresh this list." : "Connect this device to the server, then choose the printers to share.";



   /// <summary>Counts the printers the server reported in one approval state.</summary>
   private static int Count(AgentSnapshot snapshot, string serverState)
      => snapshot.Printers.Count(printer => string.Equals(printer.ServerState, serverState, StringComparison.Ordinal));
}
