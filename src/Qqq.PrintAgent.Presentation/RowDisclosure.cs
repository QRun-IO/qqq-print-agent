/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation;

/// <summary>
/// What the expand/collapse button of a table row says about itself: an accessible name that names the row, a status word
/// for assistive technology and a tooltip, each in step with whether the row's details are showing.
/// </summary>
public sealed record RowDisclosure(string Name, string ItemStatus, string ToolTip)
{
   /// <summary>Builds the texts for a row of the given kind ("printer" or "job") that shows the given subject.</summary>
   public static RowDisclosure For(string kind, string subject, bool expanded)
   {
      string action = expanded ? "Collapse" : "Expand";
      return new RowDisclosure($"{action} {kind} details for {subject}", expanded ? "Expanded" : "Collapsed", $"{action} {kind} details");
   }
}
