/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Presentation.Printers;
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
   internal string SelectedFormat => TestPrintOptions.FormatOrDefault((_format.SelectedItem as ComboBoxItem)?.Tag as string);



   /// <summary>The chosen paper or label profile identifier, or null when none is selected, in which case nothing is sent.</summary>
   internal string? SelectedProfile => (_profile.SelectedItem as ComboBoxItem)?.Tag as string;



   /// <summary>Fills the format box with what <see cref="TestPrintOptions"/> offers for the printer and preselects its preferred format, then lists the matching stock profiles; the opening text warns that the test uses real stock and needs local sharing and server approval.</summary>
   internal TestPrintEditor(PrinterSnapshot printer, string printerLabel)
   {
      _driverName = printer.DriverName;
      Spacing = 16;
      Children.Add(new TextBlock { Text = TestPrintOptions.Introduction(AppIdentity.ServerName, printerLabel), TextWrapping = TextWrapping.Wrap });
      foreach(TestPrintChoice format in TestPrintOptions.Formats(printer))
      {
         _format.Items.Add(new ComboBoxItem { Content = format.Label, Tag = format.Value });
      }
      Select(_format, TestPrintOptions.PreferredFormat(printer));
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
   /// Lists the stock profiles for the selected format, preselects the one suggested from the driver name, and shows guidance.
   /// The suggestion is only a hint: the operator must confirm the printer's real resolution, because a wrong one changes the
   /// printed size. The selection handler is detached while the list is rebuilt, so rebuilding does not fire it.
   /// </summary>
   private void UpdateProfiles()
   {
      _profile.SelectionChanged -= ProfileChanged;
      _profile.Items.Clear();
      foreach(TestPrintChoice profile in TestPrintOptions.Profiles(SelectedFormat))
      {
         _profile.Items.Add(new ComboBoxItem { Content = profile.Label, Tag = profile.Value });
      }
      Select(_profile, TestPrintOptions.PreferredProfile(SelectedFormat, _driverName));
      _profile.Header = TestPrintOptions.ProfileHeader(SelectedFormat);
      UpdateGuidance();
      _profile.SelectionChanged += ProfileChanged;
   }



   /// <summary>Shows the guidance for the current format and stock, which explains what to load and what the test will do.</summary>
   private void UpdateGuidance() => _guidance.Text = TestPrintOptions.Guidance(SelectedFormat, SelectedProfile);



   /// <summary>Selects the entry of an option box that carries the given value.</summary>
   private static void Select(ComboBox box, string value)
      => box.SelectedItem = box.Items.Cast<ComboBoxItem>().First(item => string.Equals(item.Tag as string, value, StringComparison.Ordinal));
}
