/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

/// <summary>Selected-printer information stays readable without expanding long descriptions into the table.</summary>
internal sealed class PrinterDetailsView : StackPanel
{
   private readonly TextBlock _name = Value();
   private readonly TextBlock _queue = Value();
   private readonly TextBlock _status = Value();
   private readonly TextBlock _location = Value();
   private readonly TextBlock _description = Value();
   private readonly TextBlock _technical = Value();

   internal PrinterDetailsView()
   {
      Spacing = 8;
      AddField("Name", _name);
      AddField("Windows queue", _queue);
      AddField("Status", _status);
      AddField("Location", _location);
      _description.MaxLines = 3;
      _description.TextTrimming = TextTrimming.WordEllipsis;
      AddField("Description", _description);
      Children.Add(new TextBlock { Text = "Open Settings to read or edit the full description.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
      Children.Add(new Expander { Header = "Windows diagnostics", Content = _technical, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
   }

   internal void Update(PrinterSnapshot printer, string name, string status)
   {
      _name.Text = name;
      _queue.Text = printer.Name;
      _status.Text = status;
      _location.Text = string.IsNullOrWhiteSpace(printer.Location) ? "Not set" : printer.Location;
      _description.Text = string.IsNullOrWhiteSpace(printer.Description) ? "Not set" : printer.Description;
      _technical.Text = $"Driver: {printer.DriverName}\nPort: {printer.PortName}\nPrinter ID: {printer.PrinterId}";
   }

   private void AddField(string label, TextBlock value)
   {
      Grid row = new() { ColumnSpacing = 12 };
      row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
      row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
      Grid.SetColumn(value, 1);
      row.Children.Add(value);
      Children.Add(row);
   }

   private static TextBlock Value() => new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
}
