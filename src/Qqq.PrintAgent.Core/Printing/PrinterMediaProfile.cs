/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>A measured Windows machine queue default, not a claim about physically loaded stock.</summary>
public sealed record PrinterMediaProfile(string Id, string Revision, string Name, int WidthMicrons, int HeightMicrons,
   int DpiX, int DpiY, string Source = "WINDOWS_QUEUE_DEFAULT", string ApplyMode = "DRIVER_DEFAULT")
{
   /// <summary>Refuse stale, ambiguous or unavailable selected defaults before any native submission.</summary>
   public static void RequireMatch(PrintOptions options, IReadOnlyList<PrinterMediaProfile>? profiles)
   {
      PrinterMediaSelection? selection = options.MediaProfile;
      if(selection is null)
      { return; }
      if(options.PaperWidthMm is not null || options.PaperHeightMm is not null || !ValidId(selection.Id)
         || profiles is { Count: > 32 }
         || selection.Revision is not { Length: 64 } || selection.Revision.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
      { throw new PrinterMediaMismatchException("The selected printer media profile is invalid or conflicts with explicit paper dimensions."); }
      PrinterMediaProfile[] matches = profiles?.Where(profile => string.Equals(profile.Id, selection.Id, StringComparison.Ordinal)).ToArray() ?? [];
      if(matches.Length != 1 || !string.Equals(matches[0].Revision, selection.Revision, StringComparison.Ordinal)
         || matches[0].Name is not { Length: >= 1 and <= 512 }
         || matches[0].WidthMicrons is < 1 or > 2_000_000 || matches[0].HeightMicrons is < 1 or > 2_000_000
         || matches[0].DpiX is < 1 or > 9600 || matches[0].DpiY is < 1 or > 9600
         || !string.Equals(matches[0].Source, "WINDOWS_QUEUE_DEFAULT", StringComparison.Ordinal)
         || !string.Equals(matches[0].ApplyMode, "DRIVER_DEFAULT", StringComparison.Ordinal))
      { throw new PrinterMediaMismatchException("The selected printer media profile changed or is unavailable. Refresh the printer and select its current default."); }
   }



   /// <summary>
   /// Matches the shared opaque-ID alphabet and length before profile lookup, preventing malformed selectors from reaching native submission.
   /// </summary>
   private static bool ValidId(string? id)
      => id is { Length: >= 1 and <= 128 }
         && id.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');
}
