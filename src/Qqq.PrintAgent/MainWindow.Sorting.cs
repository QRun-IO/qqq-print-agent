/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Qqq.PrintAgent;

public sealed partial class MainWindow
{
   private string _printerSortBy = "name";
   private bool _printerSortDescending;
   private string _jobSortBy = "createdAt";
   private bool _jobSortDescending = true;

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

   private void UpdatePrinterSortHeaders()
   {
      UpdateSortHeader(PrinterNameHeader, "Name", _printerSortBy, _printerSortDescending);
      UpdateSortHeader(PrinterStatusHeader, "Status", _printerSortBy, _printerSortDescending);
      UpdateSortHeader(PrinterLocationHeader, "Location", _printerSortBy, _printerSortDescending);
      UpdateSortHeader(PrinterFormatsHeader, "Formats", _printerSortBy, _printerSortDescending);
      UpdateSortHeader(PrinterSharedHeader, "Shared", _printerSortBy, _printerSortDescending);
   }

   private void UpdateJobSortHeaders(string sortBy, bool descending)
   {
      UpdateSortHeader(JobTitleHeader, "Document", sortBy, descending);
      UpdateSortHeader(JobPrinterHeader, "Printer", sortBy, descending);
      UpdateSortHeader(JobStatusHeader, "Status", sortBy, descending);
      UpdateSortHeader(JobFormatHeader, "Format", sortBy, descending);
      UpdateSortHeader(JobCreatedHeader, "Created", sortBy, descending);
   }

   private static void UpdateSortHeader(Button header, string label, string sortBy, bool descending)
   {
      bool active = string.Equals(header.Tag as string, sortBy, StringComparison.Ordinal);
      header.Content = label + (active ? descending ? " ↓" : " ↑" : "");
      AutomationProperties.SetName(header, $"Sort by {label.ToLowerInvariant()}{(active ? descending ? ", descending" : ", ascending" : "")}");
      ToolTipService.SetToolTip(header, active ? $"Sort by {label.ToLowerInvariant()} {(descending ? "ascending" : "descending")}" : $"Sort by {label.ToLowerInvariant()}");
   }

   private void SetTableHeadersEnabled(bool enabled)
   {
      foreach(Button header in new[] { PrinterNameHeader, PrinterStatusHeader, PrinterLocationHeader, PrinterFormatsHeader, PrinterSharedHeader,
         JobTitleHeader, JobPrinterHeader, JobStatusHeader, JobFormatHeader, JobCreatedHeader })
      {
         header.IsEnabled = enabled;
      }
   }

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
