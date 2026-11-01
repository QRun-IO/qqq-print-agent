/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// Per-job print options. Explicit paper dimensions and a queue media profile are mutually exclusive.
/// </summary>
public sealed record PrintOptions(int Copies = 1, string Scale = "ACTUAL_SIZE", double? PaperWidthMm = null, double? PaperHeightMm = null, PrinterMediaSelection? MediaProfile = null)
{
   /// <summary>
   /// Returns explicit job paper in microns, or null when the Windows queue default applies. Partial, non-finite,
   /// out-of-range or media-profile-conflicting requests throw before any native printer call.
   /// </summary>
   public (int WidthMicrons, int HeightMicrons)? CustomPaperMicrons()
   {
      if(PaperWidthMm is null && PaperHeightMm is null)
      {
         return null;
      }
      if(PaperWidthMm is not { } width || PaperHeightMm is not { } height || MediaProfile is not null || !ValidMillimetres(width) || !ValidMillimetres(height))
      {
         throw new InvalidDataException("Paper dimensions need a complete width and height up to 2000 mm and cannot be combined with a media profile.");
      }
      return ((int)Math.Round(width * 1000), (int)Math.Round(height * 1000));
   }



   /// <summary>
   /// Accepts only a finite positive size within the protocol's 2000 mm bound.
   /// </summary>
   private static bool ValidMillimetres(double value) => double.IsFinite(value) && value > 0 && value <= 2000;
}
