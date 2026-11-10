/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Presentation;
using Qqq.PrintAgent.Presentation.Printers;
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
   /// Applies a refreshed snapshot in place. Every text and tooltip comes ready-made from <see cref="PrinterRow"/>; this
   /// method only copies them into the controls, sets the share checkbox to what the service holds, and preserves the
   /// expanded or collapsed state.
   /// </summary>
   internal void Update(PrinterSnapshot printer, PrinterRow row, bool busy)
   {
      Snapshot = printer;
      SetCell(_name, row.Name);
      SetCell(_status, row.Status);
      SetCell(_server, row.Server);
      SetCell(_location, row.Location);
      SetCell(_formats, row.Formats);
      Exposure.IsChecked = printer.Exposed;
      _detailsView.Update(PrinterDetails.From(printer), row);
      SetBusy(busy);
      ToolTipService.SetToolTip(_name, row.NameToolTip);
      ToolTipService.SetToolTip(_status, row.StatusToolTip);
      ToolTipService.SetToolTip(_server, row.ServerDetail);
      AutomationProperties.SetName(Exposure, row.ShareName);
      SetSelected(_details.Visibility == Visibility.Visible);
   }



   /// <summary>Applies the enablement rules of <see cref="PrinterActions"/> to the share checkbox and the two action buttons for the current busy state.</summary>
   internal void SetBusy(bool busy)
   {
      Exposure.IsEnabled = PrinterActions.CanShare(Snapshot, busy);
      _settings.IsEnabled = PrinterActions.CanEdit(busy);
      _test.IsEnabled = PrinterActions.CanTest(Snapshot, busy);
   }



   /// <summary>Expands or collapses the inline details and keeps the disclosure button's glyph, accessible name, status and tooltip in step with that state.</summary>
   internal void SetSelected(bool selected)
   {
      _details.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
      _chevron.Glyph = selected ? "\uE70D" : "\uE76C";
      var disclosure = RowDisclosure.For("printer", _name.Text, selected);
      AutomationProperties.SetName(_disclosure, disclosure.Name);
      AutomationProperties.SetItemStatus(_disclosure, disclosure.ItemStatus);
      ToolTipService.SetToolTip(_disclosure, disclosure.ToolTip);
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
