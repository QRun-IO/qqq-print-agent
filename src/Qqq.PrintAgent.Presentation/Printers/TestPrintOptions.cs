/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Printers;

/// <summary>
/// The choices and wording of the test-print dialog for one printer: which formats it offers, which stock profiles go
/// with a format, which entries are preselected, and the guidance that changes with the selection. The suggestions come
/// from the driver name and are only hints; the operator confirms the loaded stock and the printer's real resolution.
/// </summary>
public static class TestPrintOptions
{
   private const string Zpl = "ZPL";



   /// <summary>The opening sentence: what will be printed, on which printer, and that it uses real stock and needs sharing and approval.</summary>
   public static string Introduction(string serverName, string printerLabel)
      => $"Print one high-contrast {serverName} diagnostic page or label on {printerLabel}. This uses real stock and requires local sharing and server approval.";



   /// <summary>The formats the printer actually supports, each with the name an operator recognises.</summary>
   public static IReadOnlyList<TestPrintChoice> Formats(PrinterSnapshot printer)
   {
      ArgumentNullException.ThrowIfNull(printer);
      return printer.Formats.Select(format => new TestPrintChoice(IsZpl(format) ? "Printer-ready label (ZPL)" : "Document (PDF)", format)).ToArray();
   }



   /// <summary>The format to preselect: ZPL when the printer supports it, because a label printer is far more likely to be tested with a label.</summary>
   public static string PreferredFormat(PrinterSnapshot printer)
   {
      ArgumentNullException.ThrowIfNull(printer);
      return TestPrintDefaults.Format(printer.Formats);
   }



   /// <summary>The format in force when the box has no selection, so the dialog always has a defined format.</summary>
   public static string FormatOrDefault(string? selected) => selected ?? "PDF";



   /// <summary>The stock profiles offered for a format: label sizes at three resolutions for ZPL, and Letter, A4 or a label size for PDF.</summary>
   public static IReadOnlyList<TestPrintChoice> Profiles(string format)
      => TestPrintDefaults.Choices(format).Select(choice => new TestPrintChoice(choice.Label, choice.Profile)).ToArray();



   /// <summary>The profile to preselect for a format, suggested from the driver name.</summary>
   public static string PreferredProfile(string format, string? driverName) => TestPrintDefaults.Profile(format, driverName);



   /// <summary>The heading of the stock box, which mentions resolution only for ZPL, where it matters.</summary>
   public static string ProfileHeader(string format) => IsZpl(format) ? "Label stock and resolution" : "Paper or label stock";



   /// <summary>
   /// Explains what to load and what the test will do. A short PDF label carries its own actual-size paper, so the Windows queue
   /// default is not changed; ZPL needs the real resolution confirmed; other paper must match the queue's paper size.
   /// </summary>
   public static string Guidance(string format, string? profile)
   {
      string stock = TestPrintDefaults.LabelStock(profile);
      if(IsZpl(format))
      {
         return $"Load {(stock.Length > 0 ? stock : "matching")} labels and verify the printer's actual resolution before printing. The default is only a driver-name suggestion (203 dpi when unknown); a wrong DPI changes the printed size. This diagnostic does not calibrate the printer or save device configuration.";
      }
      bool shortLabel = profile?.StartsWith("LABEL_4X6_", StringComparison.Ordinal) == false && stock.Length > 0;
      return shortLabel
         ? $"Load {stock} labels first. This job carries its own actual-size {stock} paper, so the Windows queue default (for example 4 × 6 in) is not changed and nothing is printed until you continue. The diagnostic prints one copy."
         : "Load stock matching the selected size and set the Windows queue to the same paper or label size. The diagnostic prints one copy at actual size; the stock selection does not change Windows printer defaults.";
   }



   /// <summary>True for the label language, the one format whose choices and guidance differ.</summary>
   private static bool IsZpl(string format) => string.Equals(format, Zpl, StringComparison.Ordinal);
}
