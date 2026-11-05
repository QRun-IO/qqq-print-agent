/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

/// <summary>A compact stable table row retains focus and selection across refreshes.</summary>
internal sealed class PrinterRowView : Grid
{
   private readonly TextBlock _name = Cell();
   private readonly TextBlock _status = Cell();
   private readonly TextBlock _server = Cell();
   private readonly TextBlock _location = Cell();
   private readonly TextBlock _formats = Cell();
   private readonly StackPanel _details = new() { Spacing = 10, Margin = new Thickness(0, 12, 0, 16), Visibility = Visibility.Collapsed, Tag = "PrinterInlineDetails" };
   private readonly PrinterDetailsView _detailsView = new();
   private readonly Button _settings = new() { Content = "Settings" };
   private readonly Button _test = new() { Content = "Print test" };
   private readonly Button _disclosure = new() { Width = 32, Height = 32, Padding = new Thickness(0) };
   private readonly FontIcon _chevron = new() { Glyph = "\uE76C", FontSize = 12 };
   internal PrinterSnapshot? Snapshot { get; private set; }
   internal CheckBox Exposure { get; } = new() { VerticalAlignment = VerticalAlignment.Center };



   /// <summary>
   /// Builds a printer row once and reuses it across refreshes, so focus and selection survive. It lays out the name,
   /// status, server approval, location, formats and the share checkbox in six columns; the callbacks come from the window, and each reads
   /// the row's current snapshot when invoked, so the row never acts on a printer it no longer shows.
   /// </summary>
   internal PrinterRowView(Func<PrinterSnapshot, bool, Task> exposure, Func<PrinterSnapshot, Task> settings, Func<PrinterSnapshot, Task> test, Action<PrinterRowView> toggle)
   {
      ColumnSpacing = 12;
      MinHeight = 44;
      RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
      RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
      _disclosure.Content = _chevron;
      _disclosure.Command = new RelayCommand(() => toggle(this));
      AutomationProperties.SetAutomationId(_disclosure, "TogglePrinterDetails");
      FrameworkElement[] cells = [NameCell(), _status, _server, _location, _formats, Exposure];
      AutomationProperties.SetAutomationId(_server, "PrinterServerState");
      for(int index = 0; index < cells.Length; index++)
      {
         SetColumn(cells[index], index);
         Children.Add(cells[index]);
      }
      Exposure.Command = new AsyncRelayCommand(() => Snapshot is PrinterSnapshot printer ? exposure(printer, Exposure.IsChecked == true) : Task.CompletedTask);
      InitializeDetails(settings, test);
   }



   /// <summary>Builds the first cell: the disclosure button beside the printer name, so expanding details is available from the keyboard and by pointer.</summary>
   private Grid NameCell()
   {
      Grid name = new() { ColumnSpacing = 4 };
      name.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
      name.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      name.Children.Add(_disclosure);
      SetColumn(_name, 1);
      name.Children.Add(_name);
      return name;
   }



   /// <summary>Creates the expandable area with the Settings and Print test actions above the read-only printer details; both actions use the row's current snapshot.</summary>
   private void InitializeDetails(Func<PrinterSnapshot, Task> settings, Func<PrinterSnapshot, Task> test)
   {
      _settings.Command = new AsyncRelayCommand(() => Snapshot is PrinterSnapshot printer ? settings(printer) : Task.CompletedTask);
      _test.Command = new AsyncRelayCommand(() => Snapshot is PrinterSnapshot printer ? test(printer) : Task.CompletedTask);
      Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_settings, "EditPrinterProfile");
      Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_test, "PrintTest");
      StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
      actions.Children.Add(_settings);
      actions.Children.Add(_test);
      _details.Children.Add(actions);
      _details.Children.Add(_detailsView);
      SetRow(_details, 1);
      SetColumnSpan(_details, 6);
      Children.Add(_details);
   }



   /// <summary>
   /// Applies a refreshed snapshot in place: the friendly name falls back to the Windows queue name, the status, server
   /// approval and formats are shown as text (never color alone), the share checkbox follows the service, and tooltips carry
   /// the full values and the unattended-printing explanation. The expanded or collapsed state is preserved.
   /// </summary>
   internal void Update(PrinterSnapshot printer, string status, string serverDetail, bool busy)
   {
      Snapshot = printer;
      SetCell(_name, string.IsNullOrWhiteSpace(printer.DisplayName) ? printer.Name : printer.DisplayName);
      SetCell(_status, StatusLabel(printer));
      SetCell(_server, ServerLabel(printer));
      SetCell(_location, string.IsNullOrWhiteSpace(printer.Location) ? "—" : printer.Location);
      SetCell(_formats, printer.Formats.Length == 0 ? "—" : string.Join(", ", printer.Formats));
      Exposure.IsChecked = printer.Exposed;
      _detailsView.Update(printer, _name.Text, _status.Text, serverDetail);
      SetBusy(busy);
      ToolTipService.SetToolTip(_name, $"{_name.Text}\nWindows queue: {printer.Name}");
      ToolTipService.SetToolTip(_status, printer.Formats.Length == 0 ? "Unavailable for unattended service printing. Select this printer for details." : status);
      ToolTipService.SetToolTip(_server, serverDetail);
      Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Exposure, $"Share {_name.Text} with the server");
      SetSelected(_details.Visibility == Visibility.Visible);
   }



   /// <summary>
   /// Enables sharing only for a printer that supports unattended printing (or one already shared, so it can be turned off),
   /// and the test print only for a shared printer that has a format. Everything is disabled while a request is running.
   /// </summary>
   internal void SetBusy(bool busy)
   {
      Exposure.IsEnabled = !busy && Snapshot is PrinterSnapshot printer && (printer.Formats.Length > 0 || printer.Exposed);
      _settings.IsEnabled = !busy;
      _test.IsEnabled = !busy && Snapshot is { Exposed: true } selected && selected.Formats.Length > 0;
   }



   /// <summary>Expands or collapses the inline details and keeps the disclosure button's glyph, accessible name, status and tooltip in step with that state.</summary>
   internal void SetSelected(bool selected)
   {
      _details.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
      _chevron.Glyph = selected ? "\uE70D" : "\uE76C";
      string action = selected ? "Collapse" : "Expand";
      AutomationProperties.SetName(_disclosure, $"{action} printer details for {_name.Text}");
      AutomationProperties.SetItemStatus(_disclosure, selected ? "Expanded" : "Collapsed");
      ToolTipService.SetToolTip(_disclosure, $"{action} printer details");
   }



   /// <summary>
   /// The status word shown for a printer. A printer with no usable format cannot print unattended, so it reads "Offline" when
   /// the spooler says so and otherwise "Interactive only" instead of a misleading ready state.
   /// </summary>
   internal static string StatusLabel(PrinterSnapshot printer) => printer.Formats.Length > 0 ? JobLabels.Status(printer.Status)
      : string.Equals(printer.Status, "OFFLINE", StringComparison.Ordinal) ? "Offline" : "Interactive only";



   /// <summary>The server-approval word shown for a printer, from the service's last catalog reading for the connected server.</summary>
   internal static string ServerLabel(PrinterSnapshot printer) => printer.ServerState switch
   {
      PrinterServerState.Approved => "Approved",
      PrinterServerState.AwaitingApproval => "Awaiting approval",
      PrinterServerState.NotReported => "Not reported",
      PrinterServerState.NotShared => "Not shared",
      _ => "Not checked",
   };



   /// <summary>
   /// The sentence behind the server-approval word: what it means and, for a reading that failed, the service's reason.
   /// The server is named so a printer shared before a server change is never read as approved by the new one.
   /// </summary>
   internal static string ServerDetail(PrinterSnapshot printer, PrinterServerSync? sync, string serverName)
   {
      string server = string.IsNullOrEmpty(sync?.Server) ? serverName : $"{serverName} ({sync!.Server})";
      string freshness = string.Equals(sync?.Source, PrinterServerSync.RealtimeSource, StringComparison.Ordinal) ? " The server pushes a change the moment an administrator makes it." : " Checked about once a minute while connected.";
      return printer.ServerState switch
      {
         PrinterServerState.Approved => $"{server} approved this printer for this computer; it can receive jobs.{freshness}",
         PrinterServerState.AwaitingApproval => $"{server} received this printer in the inventory but has not approved it. An administrator approves it on the server (Printing → Print devices).{freshness}",
         PrinterServerState.NotReported => $"{server} did not report approval: {sync?.Detail ?? "the catalog could not be read."} Approve or check the printer on the server.",
         PrinterServerState.NotShared => "Not shared with the server. Turn on Shared to offer it; the server then has to approve it.",
         _ => $"Not checked yet with {server}. The service asks the server after it connects and sends the inventory.",
      };
   }



   /// <summary>Creates one single-line, centered, ellipsized text cell.</summary>
   private static TextBlock Cell() => new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };



   /// <summary>Sets a cell's text and its tooltip together, so a value cut off by ellipsis can still be read in full.</summary>
   private static void SetCell(TextBlock cell, string text)
   {
      cell.Text = text;
      ToolTipService.SetToolTip(cell, text);
   }
}
