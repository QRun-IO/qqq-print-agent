/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Qqq.PrintAgent.Windows;


namespace Qqq.PrintAgent;

/// <summary>A stable printer row retains keyboard focus and expanded details while status changes.</summary>
internal sealed class PrinterRowView : StackPanel
{
   private readonly TextBlock _name = new() { FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
   private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
   private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
   internal CheckBox Exposure { get; } = new() { Content = "Share with QQQ", VerticalAlignment = VerticalAlignment.Center };
   internal Button TestPrint { get; } = new() { Content = "Print test", HorizontalAlignment = HorizontalAlignment.Left };

   internal PrinterRowView(Func<PrinterSnapshot, bool, Task> exposure, Func<PrinterSnapshot, Task> print, Brush secondary)
   {
      Spacing = 10;
      Margin = new Thickness(4, 12, 4, 12);
      _status.Foreground = secondary;
      _details.Foreground = secondary;
      Exposure.Command = new AsyncRelayCommand(() => Exposure.Tag is PrinterSnapshot printer ? exposure(printer, Exposure.IsChecked == true) : Task.CompletedTask);
      TestPrint.Command = new AsyncRelayCommand(() => TestPrint.Tag is PrinterSnapshot printer ? print(printer) : Task.CompletedTask);
      Grid heading = new() { ColumnSpacing = 20 };
      heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
      StackPanel label = new() { Spacing = 4 };
      label.Children.Add(_name);
      label.Children.Add(_status);
      heading.Children.Add(label);
      Grid.SetColumn(Exposure, 1);
      heading.Children.Add(Exposure);
      Children.Add(heading);
      Children.Add(TestPrint);
      Children.Add(new Expander { Header = "Printer details", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = _details });
   }

   internal void Update(PrinterSnapshot printer, string status, bool busy)
   {
      _name.Text = printer.Name;
      _status.Text = printer.Formats.Length == 0
         ? string.Equals(printer.Status, "OFFLINE", StringComparison.Ordinal)
            ? "Offline · Printer is unavailable to the service"
            : "Requires interactive printing; unavailable to the service"
         : $"{status} · {string.Join(", ", printer.Formats)}";
      _details.Text = $"Driver: {printer.DriverName}\nPort: {printer.PortName}\nPrinter ID: {printer.PrinterId}";
      Exposure.Tag = printer;
      Exposure.IsChecked = printer.Exposed;
      TestPrint.Tag = printer;
      SetBusy(busy);
      ToolTipService.SetToolTip(TestPrint, printer.Exposed ? "Send one test job through QQQ" : "Share this printer and approve it in QQQ before testing");
      Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Exposure, $"Share {printer.Name} with QQQ");
      Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TestPrint, $"Print test on {printer.Name}");
   }

   internal void SetBusy(bool busy)
   {
      Exposure.IsEnabled = !busy && Exposure.Tag is PrinterSnapshot printer && (printer.Formats.Length > 0 || printer.Exposed);
      TestPrint.IsEnabled = !busy && TestPrint.Tag is PrinterSnapshot target && target.Exposed && target.Formats.Length > 0;
   }
}
