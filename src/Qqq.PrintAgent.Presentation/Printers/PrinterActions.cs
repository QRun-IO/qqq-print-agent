/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Printers;

/// <summary>When each control of a printer row may be used. The rules live here, not in the row view, so that "why is this button disabled" has one tested answer.</summary>
public static class PrinterActions
{
   /// <summary>
   /// Sharing can be changed for a printer that supports unattended printing, and for one that is already shared (so it can
   /// always be turned off), but never while an administration request is running or before the row has a printer.
   /// </summary>
   public static bool CanShare(PrinterSnapshot? printer, bool busy) => !busy && printer is not null && (printer.Formats.Length > 0 || printer.Exposed);



   /// <summary>The printer's details can be edited whenever no administration request is running.</summary>
   public static bool CanEdit(bool busy) => !busy;



   /// <summary>A test can be printed only on a shared printer that has a format the service can send, and not while a request is running.</summary>
   public static bool CanTest(PrinterSnapshot? printer, bool busy) => !busy && printer is { Exposed: true } && printer.Formats.Length > 0;
}
