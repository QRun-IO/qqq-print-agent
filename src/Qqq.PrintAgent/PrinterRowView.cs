/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

/// <summary>A compact stable table row retains focus and selection across refreshes.</summary>
internal sealed class PrinterRowView : Grid
{
   private readonly TextBlock _name = Cell();
   private readonly TextBlock _status = Cell();
   private readonly TextBlock _location = Cell();
   private readonly TextBlock _formats = Cell();
   private readonly StackPanel _details = new() { Spacing = 10, Margin = new Thickness(0, 12, 0, 16), Visibility = Visibility.Collapsed, Tag = "PrinterInlineDetails" };
   private readonly PrinterDetailsView _detailsView = new();
   private readonly Button _settings = new() { Content = "Settings" };
   private readonly Button _test = new() { Content = "Print test" };
   internal PrinterSnapshot? Snapshot { get; private set; }
   internal CheckBox Exposure { get; } = new() { VerticalAlignment = VerticalAlignment.Center };

   internal PrinterRowView(Func<PrinterSnapshot, bool, Task> exposure, Func<PrinterSnapshot, Task> settings, Func<PrinterSnapshot, Task> test)
   {
      ColumnSpacing = 12;
      MinHeight = 44;
      RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
      RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
      ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
      FrameworkElement[] cells = [_name, _status, _location, _formats, Exposure];
      for(int index = 0; index < cells.Length; index++)
      {
         SetColumn(cells[index], index);
         Children.Add(cells[index]);
      }
      Exposure.Command = new AsyncRelayCommand(() => Snapshot is PrinterSnapshot printer ? exposure(printer, Exposure.IsChecked == true) : Task.CompletedTask);
      InitializeDetails(settings, test);
   }

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
      SetColumnSpan(_details, 5);
      Children.Add(_details);
   }

   internal void Update(PrinterSnapshot printer, string status, bool busy)
   {
      Snapshot = printer;
      SetCell(_name, string.IsNullOrWhiteSpace(printer.DisplayName) ? printer.Name : printer.DisplayName);
      SetCell(_status, StatusLabel(printer));
      SetCell(_location, string.IsNullOrWhiteSpace(printer.Location) ? "—" : printer.Location);
      SetCell(_formats, printer.Formats.Length == 0 ? "—" : string.Join(", ", printer.Formats));
      Exposure.IsChecked = printer.Exposed;
      _detailsView.Update(printer, _name.Text, _status.Text);
      SetBusy(busy);
      ToolTipService.SetToolTip(_name, $"{_name.Text}\nWindows queue: {printer.Name}");
      ToolTipService.SetToolTip(_status, printer.Formats.Length == 0 ? "Unavailable for unattended service printing. Select this printer for details." : status);
      Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Exposure, $"Share {_name.Text} with the server");
   }

   internal void SetBusy(bool busy)
   {
      Exposure.IsEnabled = !busy && Snapshot is PrinterSnapshot printer && (printer.Formats.Length > 0 || printer.Exposed);
      _settings.IsEnabled = !busy;
      _test.IsEnabled = !busy && Snapshot is { Exposed: true } selected && selected.Formats.Length > 0;
   }

   internal void SetSelected(bool selected) => _details.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;

   internal static string StatusLabel(PrinterSnapshot printer) => printer.Formats.Length > 0 ? JobLabels.Status(printer.Status)
      : string.Equals(printer.Status, "OFFLINE", StringComparison.Ordinal) ? "Offline" : "Interactive only";

   private static TextBlock Cell() => new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };

   private static void SetCell(TextBlock cell, string text)
   {
      cell.Text = text;
      ToolTipService.SetToolTip(cell, text);
   }
}
