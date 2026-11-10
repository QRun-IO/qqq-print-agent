/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Printers;

/// <summary>
/// Which column the printer table is sorted by, in which direction, and how two printers compare under it. Printers sort
/// locally because the whole list is already loaded. Text is compared the way the user's language orders it, ignoring
/// case, and ties fall back to the printer identifier so equal rows never swap places between refreshes.
/// </summary>
public sealed class PrinterSort
{
   private readonly StringComparer _text;



   /// <summary>Starts sorted by name, ascending. The comparer defaults to the current culture ignoring case; tests pass a fixed one.</summary>
   public PrinterSort(StringComparer? text = null)
   {
      _text = text ?? StringComparer.CurrentCultureIgnoreCase;
   }



   /// <summary>The key of the active column: name, status, server, location, formats or shared.</summary>
   public string SortBy { get; private set; } = "name";



   /// <summary>True when the active column is sorted from last to first.</summary>
   public bool Descending { get; private set; }



   /// <summary>Applies a click on a column header: a new column starts ascending, and clicking the active column reverses it.</summary>
   public void Toggle(string key)
   {
      ArgumentNullException.ThrowIfNull(key);
      Descending = string.Equals(SortBy, key, StringComparison.Ordinal) && !Descending;
      SortBy = key;
   }



   /// <summary>
   /// Compares two printers by the active column, using the same labels the table shows so the order matches what is read.
   /// A row that has no printer yet compares equal to anything. Equal values are ordered by printer identifier, always
   /// ascending, which keeps the order stable in both directions.
   /// </summary>
   public int Compare(PrinterSnapshot? left, PrinterSnapshot? right)
   {
      if(left is null || right is null)
      {
         return 0;
      }
      int comparison = SortBy switch
      {
         "status" => _text.Compare(PrinterRow.StatusLabel(left), PrinterRow.StatusLabel(right)),
         "server" => _text.Compare(PrinterRow.ServerLabel(left), PrinterRow.ServerLabel(right)),
         "location" => _text.Compare(left.Location, right.Location),
         "formats" => _text.Compare(string.Join(", ", left.Formats), string.Join(", ", right.Formats)),
         "shared" => left.Exposed.CompareTo(right.Exposed),
         _ => _text.Compare(PrinterRow.DisplayName(left), PrinterRow.DisplayName(right)),
      };
      return comparison == 0 ? StringComparer.Ordinal.Compare(left.PrinterId, right.PrinterId) : Descending ? -comparison : comparison;
   }
}
