/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Qqq.PrintAgent.Presentation;
using Qqq.PrintAgent.Presentation.Printers;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

/// <summary>
/// Sorting part of the main window: clickable column headers for the printer and job tables. Printers sort locally because
/// the list is small and already loaded; jobs ask the service to sort the whole matching history before paging, so the
/// order is the same on every page. Which column and direction is active is kept by <see cref="PrinterSort"/> and the job
/// list state; this part applies clicks to them and moves rows and header texts.
/// </summary>
public sealed partial class MainWindow
{
   private readonly PrinterSort _printerSort = new();



   /// <summary>Sorts the printer list by the clicked column, reversing the direction when the same column is clicked again. Ignored while a request runs or the window closes.</summary>
   private void PrinterSort_Click(object sender, RoutedEventArgs e)
   {
      if(_busy || _updatesClosed || sender is not Button { Tag: string key })
      {
         return;
      }
      _printerSort.Toggle(key);
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
      _jobs.Sort(key);
      InvalidateJobDetails();
      await RefreshJobPageAsync();
   }



   /// <summary>Refreshes every printer column header to show the active column and direction.</summary>
   private void UpdatePrinterSortHeaders()
   {
      UpdateSortHeader(PrinterNameHeader, "Name", _printerSort.SortBy, _printerSort.Descending);
      UpdateSortHeader(PrinterStatusHeader, "Status", _printerSort.SortBy, _printerSort.Descending);
      UpdateSortHeader(PrinterServerHeader, "Server", _printerSort.SortBy, _printerSort.Descending);
      UpdateSortHeader(PrinterLocationHeader, "Location", _printerSort.SortBy, _printerSort.Descending);
      UpdateSortHeader(PrinterFormatsHeader, "Formats", _printerSort.SortBy, _printerSort.Descending);
      UpdateSortHeader(PrinterSharedHeader, "Shared", _printerSort.SortBy, _printerSort.Descending);
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



   /// <summary>Marks one header as active or not, with the text, accessible name and tooltip that <see cref="SortHeader"/> gives for its column key.</summary>
   private static void UpdateSortHeader(Button header, string label, string sortBy, bool descending)
   {
      var text = SortHeader.For(label, header.Tag as string, sortBy, descending);
      header.Content = text.Content;
      AutomationProperties.SetName(header, text.AutomationName);
      ToolTipService.SetToolTip(header, text.ToolTip);
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
   /// Reorders the existing printer rows in place by the active sort, moving only rows that are out of position. The selected
   /// row and the keyboard focus are restored if the move disturbed them, so sorting never loses the user's place.
   /// </summary>
   private void SortPrinterRows()
   {
      var selected = PrintersList.SelectedItem as PrinterRowView;
      var focused = FocusManager.GetFocusedElement(Content.XamlRoot) as Control;
      PrinterRowView[] sorted = _printerRows.Values.OrderBy(row => row.Snapshot, Comparer<PrinterSnapshot?>.Create(_printerSort.Compare)).ToArray();
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
}
