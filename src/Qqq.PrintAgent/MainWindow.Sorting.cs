/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Qqq.PrintAgent;

/// <summary>
/// Sorting part of the main window: clickable column headers for the printer and job tables. Printers sort locally because
/// the list is small and already loaded; jobs ask the service to sort the whole matching history before paging, so the
/// order is the same on every page. The header shows the active column and direction in text and in its accessible name.
/// </summary>
public sealed partial class MainWindow
{
   private string _printerSortBy = "name";
   private bool _printerSortDescending;
   private string _jobSortBy = "createdAt";
   private bool _jobSortDescending = true;



   /// <summary>Sorts the printer list by the clicked column, reversing the direction when the same column is clicked again. Ignored while a request runs or the window closes.</summary>
   private void PrinterSort_Click(object sender, RoutedEventArgs e)
   {
      if(_busy || _updatesClosed || sender is not Button { Tag: string key })
      {
         return;
      }
      _printerSortDescending = string.Equals(_printerSortBy, key, StringComparison.Ordinal) && !_printerSortDescending;
      _printerSortBy = key;
      UpdatePrinterSortHeaders();
      SortPrinterRows();
   }



   /// <summary>
   /// Sorts the job history by the clicked column. A new column starts ascending, except Created, which starts newest first;
   /// clicking the active column reverses it. The page offset resets to the first page and the service runs the query, so the
   /// order applies to the whole result set rather than the rows currently shown.
   /// </summary>
   private async void JobSort_Click(object sender, RoutedEventArgs e)
   {
      if(_busy || _updatesClosed || sender is not Button { Tag: string key })
      {
         return;
      }
      _jobSortDescending = string.Equals(_jobSortBy, key, StringComparison.Ordinal) ? !_jobSortDescending : string.Equals(key, "createdAt", StringComparison.Ordinal);
      _jobSortBy = key;
      InvalidateJobDetails();
      _jobOffset = 0;
      await RefreshJobPageAsync();
   }



   /// <summary>Refreshes every printer column header to show the active column and direction.</summary>
   private void UpdatePrinterSortHeaders()
   {
      UpdateSortHeader(PrinterNameHeader, "Name", _printerSortBy, _printerSortDescending);
      UpdateSortHeader(PrinterStatusHeader, "Status", _printerSortBy, _printerSortDescending);
      UpdateSortHeader(PrinterLocationHeader, "Location", _printerSortBy, _printerSortDescending);
      UpdateSortHeader(PrinterFormatsHeader, "Formats", _printerSortBy, _printerSortDescending);
      UpdateSortHeader(PrinterSharedHeader, "Shared", _printerSortBy, _printerSortDescending);
   }



   /// <summary>Refreshes every job column header from the sort the service actually applied, so the arrow never shows an order that failed to load.</summary>
   private void UpdateJobSortHeaders(string sortBy, bool descending)
   {
      UpdateSortHeader(JobTitleHeader, "Document", sortBy, descending);
      UpdateSortHeader(JobPrinterHeader, "Printer", sortBy, descending);
      UpdateSortHeader(JobStatusHeader, "Status", sortBy, descending);
      UpdateSortHeader(JobFormatHeader, "Format", sortBy, descending);
      UpdateSortHeader(JobCreatedHeader, "Created", sortBy, descending);
   }



   /// <summary>
   /// Marks one header as active or not. The direction appears as an arrow in the text and as words in the accessible name
   /// and tooltip, so it is not conveyed by the arrow glyph alone, and the tooltip names what the next click will do.
   /// </summary>
   private static void UpdateSortHeader(Button header, string label, string sortBy, bool descending)
   {
      bool active = string.Equals(header.Tag as string, sortBy, StringComparison.Ordinal);
      header.Content = label + (active ? descending ? " ↓" : " ↑" : "");
      AutomationProperties.SetName(header, $"Sort by {label.ToLowerInvariant()}{(active ? descending ? ", descending" : ", ascending" : "")}");
      ToolTipService.SetToolTip(header, active ? $"Sort by {label.ToLowerInvariant()} {(descending ? "ascending" : "descending")}" : $"Sort by {label.ToLowerInvariant()}");
   }



   /// <summary>Enables or disables every table header together, so a sort cannot be started while another request is running.</summary>
   private void SetTableHeadersEnabled(bool enabled)
   {
      foreach(Button header in new[] { PrinterNameHeader, PrinterStatusHeader, PrinterLocationHeader, PrinterFormatsHeader, PrinterSharedHeader,
         JobTitleHeader, JobPrinterHeader, JobStatusHeader, JobFormatHeader, JobCreatedHeader })
      {
         header.IsEnabled = enabled;
      }
   }



   /// <summary>
   /// Reorders the existing printer rows in place, moving only rows that are out of position. The selected row and the
   /// keyboard focus are restored if the move disturbed them, so sorting never loses the user's place.
   /// </summary>
   private void SortPrinterRows()
   {
      var selected = PrintersList.SelectedItem as PrinterRowView;
      var focused = FocusManager.GetFocusedElement(Content.XamlRoot) as Control;
      PrinterRowView[] sorted = _printerRows.Values.OrderBy(row => row, Comparer<PrinterRowView>.Create(ComparePrinters)).ToArray();
      for(int index = 0; index < sorted.Length; index++)
      {
         if(PrintersList.Items.IndexOf(sorted[index]) != index)
         {
            PrintersList.Items.Remove(sorted[index]);
            PrintersList.Items.Insert(index, sorted[index]);
         }
      }
      if(selected is not null && !ReferenceEquals(selected, PrintersList.SelectedItem))
      {
         PrintersList.SelectedItem = selected;
      }
      if(focused?.XamlRoot is not null && !ReferenceEquals(focused, FocusManager.GetFocusedElement(Content.XamlRoot)))
      {
         _ = focused.Focus(FocusState.Programmatic);
      }
   }



   /// <summary>
   /// Compares two printers by the active column using the user's culture and ignoring case, falling back to the printer
   /// identifier when they tie, so the order is deterministic and equal rows never swap places between refreshes.
   /// </summary>
   private int ComparePrinters(PrinterRowView left, PrinterRowView right)
   {
      if(left.Snapshot is not { } first || right.Snapshot is not { } second)
      {
         return 0;
      }
      StringComparer text = StringComparer.CurrentCultureIgnoreCase;
      int comparison = _printerSortBy switch
      {
         "status" => text.Compare(PrinterRowView.StatusLabel(first), PrinterRowView.StatusLabel(second)),
         "location" => text.Compare(first.Location, second.Location),
         "formats" => text.Compare(string.Join(", ", first.Formats), string.Join(", ", second.Formats)),
         "shared" => first.Exposed.CompareTo(second.Exposed),
         _ => text.Compare(PrinterLabel(first), PrinterLabel(second)),
      };
      return comparison == 0 ? StringComparer.Ordinal.Compare(first.PrinterId, second.PrinterId) : _printerSortDescending ? -comparison : comparison;
   }
}
