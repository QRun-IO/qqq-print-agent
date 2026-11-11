/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// A health level as it is shown: the level, the name of a glyph and the level in words. The view turns the level
/// into a colour, and the glyph and the words differ for every level, so the colour is never the only thing that
/// tells one state from another (ADR-0005, "Accessibility"). A glyph's name is the name of a shape; the view
/// chooses the symbol that draws it.
/// </summary>
public sealed record HealthBadge(HealthLevel Level, string GlyphName, string Text)
{
   /// <summary>The badge of each level. A level this build does not list is shown as unknown, never as healthy.</summary>
   public static HealthBadge For(HealthLevel level) => level switch
   {
      HealthLevel.Green => new HealthBadge(HealthLevel.Green, "CheckMark", "Ready"),
      HealthLevel.Orange => new HealthBadge(HealthLevel.Orange, "Warning", "Not ready"),
      HealthLevel.Red => new HealthBadge(HealthLevel.Red, "Error", "Needs attention"),
      _ => new HealthBadge(HealthLevel.Unknown, "Unknown", "Unknown"),
   };
}
