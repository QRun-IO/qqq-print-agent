/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Presentation.Printers;

namespace Qqq.PrintAgent;

/// <summary>Selected-printer information stays readable without expanding long descriptions into the table.</summary>
internal sealed class PrinterDetailsView : StackPanel
{
   private readonly TextBlock _name = Value();
   private readonly TextBlock _queue = Value();
   private readonly TextBlock _status = Value();
   private readonly TextBlock _server = Value();
   private readonly TextBlock _location = Value();
   private readonly TextBlock _description = Value();
   private readonly TextBlock _technical = Value();



   /// <summary>Builds the read-only field list: name, Windows queue, status, location and a three-line description (full text is in Settings), plus a collapsed expander for driver, port and identifier, which carries an automation identifier like every control an operator can act on.</summary>
   internal PrinterDetailsView()
   {
      Spacing = 8;
      AddField("Name", _name);
      AddField("Windows queue", _queue);
      AddField("Status", _status);
      AddField("Server", _server);
      AddField("Location", _location);
      _description.MaxLines = 3;
      _description.TextTrimming = TextTrimming.WordEllipsis;
      AddField("Description", _description);
      Children.Add(new TextBlock { Text = "Open Settings to read or edit the full description.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
      Expander diagnostics = new() { Header = "Windows diagnostics", Content = _technical, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
      Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(diagnostics, "PrinterDiagnostics");
      Children.Add(diagnostics);
   }



   /// <summary>Fills the fields from the prepared row and details texts; "Not set" for an empty location or description is decided there, so a blank is never mistaken for a missing control.</summary>
   internal void Update(PrinterDetails details, PrinterRow row)
   {
      _name.Text = row.Name;
      _queue.Text = details.Queue;
      _status.Text = row.Status;
      _server.Text = row.ServerDetail;
      _location.Text = details.Location;
      _description.Text = details.Description;
      _technical.Text = details.Technical;
   }



   /// <summary>Adds one label and value row with a fixed label column, so values line up and long values wrap instead of stretching the row.</summary>
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



   /// <summary>Creates a wrapping, selectable text block, so an operator can copy a value such as the printer identifier.</summary>
   private static TextBlock Value() => new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
}
