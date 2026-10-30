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
   internal PrinterSnapshot? Snapshot { get; private set; }
   internal CheckBox Exposure { get; } = new() { VerticalAlignment = VerticalAlignment.Center };

   internal PrinterRowView(Func<PrinterSnapshot, bool, Task> exposure)
   {
      ColumnSpacing = 12;
      MinHeight = 44;
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
   }

   internal void Update(PrinterSnapshot printer, string status, bool busy)
   {
      Snapshot = printer;
      SetCell(_name, string.IsNullOrWhiteSpace(printer.DisplayName) ? printer.Name : printer.DisplayName);
      SetCell(_status, printer.Formats.Length > 0 ? status : string.Equals(printer.Status, "OFFLINE", StringComparison.Ordinal)
         ? "Offline" : "Interactive only");
      SetCell(_location, string.IsNullOrWhiteSpace(printer.Location) ? "—" : printer.Location);
      SetCell(_formats, printer.Formats.Length == 0 ? "—" : string.Join(", ", printer.Formats));
      Exposure.IsChecked = printer.Exposed;
      SetBusy(busy);
      ToolTipService.SetToolTip(_name, $"{_name.Text}\nWindows queue: {printer.Name}");
      ToolTipService.SetToolTip(_status, printer.Formats.Length == 0 ? "Unavailable for unattended service printing. Select this printer for details." : status);
      Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Exposure, $"Share {_name.Text} with the server");
   }

   internal void SetBusy(bool busy) => Exposure.IsEnabled = !busy && Snapshot is PrinterSnapshot printer && (printer.Formats.Length > 0 || printer.Exposed);

   private static TextBlock Cell() => new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };

   private static void SetCell(TextBlock cell, string text)
   {
      cell.Text = text;
      ToolTipService.SetToolTip(cell, text);
   }
}
