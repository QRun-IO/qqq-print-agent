/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

/// <summary>Explicit stock and resolution choices for one server-generated diagnostic document.</summary>
internal sealed class TestPrintEditor : StackPanel
{
   private readonly ComboBox _format = new() { Header = "Print format", HorizontalAlignment = HorizontalAlignment.Stretch };
   private readonly ComboBox _profile = new() { Header = "Paper or label stock", HorizontalAlignment = HorizontalAlignment.Stretch };
   private readonly TextBlock _guidance = new() { TextWrapping = TextWrapping.Wrap };
   private readonly string _driverName;



   /// <summary>The chosen document format, defaulting to PDF when nothing is selected.</summary>
   internal string SelectedFormat => (_format.SelectedItem as ComboBoxItem)?.Tag as string ?? "PDF";



   /// <summary>The chosen paper or label profile identifier, or null when none is selected, in which case nothing is sent.</summary>
   internal string? SelectedProfile => (_profile.SelectedItem as ComboBoxItem)?.Tag as string;



   /// <summary>Builds the choices from the formats the printer actually supports and preselects the preferred format, then lists matching stock profiles; the text warns that the test uses real stock and needs local sharing and server approval.</summary>
   internal TestPrintEditor(PrinterSnapshot printer, string printerLabel)
   {
      _driverName = printer.DriverName;
      Spacing = 16;
      Children.Add(new TextBlock { Text = $"Print one high-contrast QQQ diagnostic page or label on {printerLabel}. This uses real stock and requires local sharing and server approval.", TextWrapping = TextWrapping.Wrap });
      foreach(string format in printer.Formats)
      {
         _format.Items.Add(new ComboBoxItem { Content = string.Equals(format, "ZPL", StringComparison.Ordinal) ? "Printer-ready label (ZPL)" : "Document (PDF)", Tag = format });
      }
      string preferred = TestPrintDefaults.Format(printer.Formats);
      _format.SelectedItem = _format.Items.Cast<ComboBoxItem>().First(item => string.Equals(item.Tag as string, preferred, StringComparison.Ordinal));
      _format.SelectionChanged += FormatChanged;
      Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_format, "TestPrintFormat");
      Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_profile, "TestPrintMedia");
      Children.Add(_format);
      Children.Add(_profile);
      Children.Add(_guidance);
      UpdateProfiles();
   }



   /// <summary>Rebuilds the stock list when the format changes, because label and paper formats offer different profiles.</summary>
   private void FormatChanged(object sender, SelectionChangedEventArgs e) => UpdateProfiles();



   /// <summary>Refreshes the guidance text when the stock choice changes, because the instructions depend on the label size.</summary>
   private void ProfileChanged(object sender, SelectionChangedEventArgs e) => UpdateGuidance();



   /// <summary>
   /// Lists the stock profiles for the selected format (4x6, 4x2 and 4x1 labels at three resolutions for ZPL; Letter, A4 or a label
   /// size for PDF), preselects the one suggested from the driver name, and shows guidance. The suggestion is only a hint: the
   /// operator must confirm the printer's real resolution, because a wrong one changes the printed size.
   /// </summary>
   private void UpdateProfiles()
   {
      _profile.SelectionChanged -= ProfileChanged;
      _profile.Items.Clear();
      bool zpl = string.Equals(SelectedFormat, "ZPL", StringComparison.Ordinal);
      foreach((string label, string value) in TestPrintDefaults.Choices(SelectedFormat))
      {
         AddProfile(label, value);
      }
      string preferred = TestPrintDefaults.Profile(SelectedFormat, _driverName);
      _profile.SelectedItem = _profile.Items.Cast<ComboBoxItem>().First(item => string.Equals(item.Tag as string, preferred, StringComparison.Ordinal));
      _profile.Header = zpl ? "Label stock and resolution" : "Paper or label stock";
      UpdateGuidance();
      _profile.SelectionChanged += ProfileChanged;
   }



   /// <summary>
   /// Explains what to load and what the test will do. A short PDF label carries its own actual-size paper, so the Windows queue
   /// default is not changed; ZPL needs the real resolution confirmed; other paper must match the queue's paper size.
   /// </summary>
   private void UpdateGuidance()
   {
      string stock = TestPrintDefaults.LabelStock(SelectedProfile);
      bool shortPdf = !string.Equals(SelectedFormat, "ZPL", StringComparison.Ordinal) && (SelectedProfile?.StartsWith("LABEL_4X6_", StringComparison.Ordinal) == false) && stock.Length > 0;
      _guidance.Text = string.Equals(SelectedFormat, "ZPL", StringComparison.Ordinal)
         ? $"Load {(stock.Length > 0 ? stock : "matching")} labels and verify the printer's actual resolution before printing. The default is only a driver-name suggestion (203 dpi when unknown); a wrong DPI changes the printed size. This diagnostic does not calibrate the printer or save device configuration."
         : shortPdf
            ? $"Load {stock} labels first. This job carries its own actual-size {stock} paper, so the Windows queue default (for example 4 × 6 in) is not changed and nothing is printed until you continue. The diagnostic prints one copy."
            : "Load stock matching the selected size and set the Windows queue to the same paper or label size. The diagnostic prints one copy at actual size; the stock selection does not change Windows printer defaults.";
   }



   /// <summary>Adds one stock choice, showing its label and carrying its profile identifier as the tag.</summary>
   private void AddProfile(string label, string value) => _profile.Items.Add(new ComboBoxItem { Content = label, Tag = value });
}
