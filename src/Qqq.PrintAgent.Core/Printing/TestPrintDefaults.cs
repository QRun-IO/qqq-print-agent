/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>Suggested diagnostic defaults; the operator must still verify installed stock and resolution.</summary>
public static class TestPrintDefaults
{
   /// <summary>Prefers ZPL when the printer supports it and otherwise PDF, because a label printer is far more likely to be tested with a label than a letter page.</summary>
   public static string Format(IEnumerable<string> supportedFormats)
      => supportedFormats.Contains("ZPL", StringComparer.Ordinal) ? "ZPL" : "PDF";



   /// <summary>Suggests a stock profile: LETTER for PDF, and for ZPL a 4x6 label at the resolution named in the driver (600, 300, otherwise 203 dpi). It is only a suggestion; the operator confirms the installed stock.</summary>
   public static string Profile(string format, string? driverName)
   {
      if(!string.Equals(format, "ZPL", StringComparison.Ordinal))
      {
         return "LETTER";
      }
      string driver = driverName ?? string.Empty;
      if(driver.Contains("600dpi", StringComparison.OrdinalIgnoreCase) || driver.Contains("600 dpi", StringComparison.OrdinalIgnoreCase))
      {
         return "LABEL_4X6_600";
      }
      return driver.Contains("300dpi", StringComparison.OrdinalIgnoreCase) || driver.Contains("300 dpi", StringComparison.OrdinalIgnoreCase)
         ? "LABEL_4X6_300" : "LABEL_4X6_203";
   }
}
