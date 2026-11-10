/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation;

/// <summary>
/// What one sortable column header shows. The direction appears as an arrow in the text and as words in the accessible
/// name, so it is not conveyed by the arrow glyph alone, and the tooltip names what the next click will do.
/// </summary>
public sealed record SortHeader(string Content, string AutomationName, string ToolTip)
{
   /// <summary>Builds the header for a column with the given label and key, under the sort that is currently applied.</summary>
   public static SortHeader For(string label, string? key, string sortBy, bool descending)
   {
      ArgumentNullException.ThrowIfNull(label);
      bool active = string.Equals(key, sortBy, StringComparison.Ordinal);
      string subject = label.ToLowerInvariant();
      return new SortHeader(
         label + (active ? descending ? " ↓" : " ↑" : ""),
         $"Sort by {subject}{(active ? descending ? ", descending" : ", ascending" : "")}",
         active ? $"Sort by {subject} {(descending ? "ascending" : "descending")}" : $"Sort by {subject}");
   }
}
