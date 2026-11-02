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



   /// <summary>Friendly stock choices as (label, server profile id). 4 × 6 stays first so the existing defaults are unchanged; 4 × 2 and 4 × 1 follow, at three resolutions for ZPL and one actual-size choice each for PDF.</summary>
   public static IReadOnlyList<(string Label, string Profile)> Choices(string format)
   {
      if(!string.Equals(format, "ZPL", StringComparison.Ordinal))
      {
         // PDF is vector: one choice per stock; the profile's dpi suffix is not used for PDF.
         return
         [
            ("US Letter — 8.5 × 11 in", "LETTER"), ("A4 — 210 × 297 mm", "A4"), ("4 × 6 in label", "LABEL_4X6_300"),
            ("4 × 2 in label", "LABEL_4X2_300"), ("4 × 1 in label", "LABEL_4X1_300"),
         ];
      }
      List<(string, string)> labels = [];
      foreach((string size, string id) in new[] { ("4 × 6", "4X6"), ("4 × 2", "4X2"), ("4 × 1", "4X1") })
      {
         foreach(int dpi in new[] { 203, 300, 600 })
         {
            labels.Add(($"{size} in label — {dpi} dpi", $"LABEL_{id}_{dpi}"));
         }
      }
      return labels;
   }



   /// <summary>Stock the operator must physically load for a profile, so the guidance can name it; empty for page stock where nothing special is loaded.</summary>
   public static string LabelStock(string? profile)
   {
      foreach((string stock, string prefix) in new[] { ("4 × 6 in", "LABEL_4X6_"), ("4 × 2 in", "LABEL_4X2_"), ("4 × 1 in", "LABEL_4X1_") })
      {
         if(profile?.StartsWith(prefix, StringComparison.Ordinal) == true)
         {
            return stock;
         }
      }
      return string.Empty;
   }
}
