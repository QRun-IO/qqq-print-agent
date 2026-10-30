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

   internal string SelectedFormat => (_format.SelectedItem as ComboBoxItem)?.Tag as string ?? "PDF";
   internal string? SelectedProfile => (_profile.SelectedItem as ComboBoxItem)?.Tag as string;

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

   private void FormatChanged(object sender, SelectionChangedEventArgs e) => UpdateProfiles();

   private void UpdateProfiles()
   {
      _profile.Items.Clear();
      bool zpl = string.Equals(SelectedFormat, "ZPL", StringComparison.Ordinal);
      if(zpl)
      {
         AddProfile("4 × 6 in label — 203 dpi", "LABEL_4X6_203");
         AddProfile("4 × 6 in label — 300 dpi", "LABEL_4X6_300");
         AddProfile("4 × 6 in label — 600 dpi", "LABEL_4X6_600");
      }
      else
      {
         AddProfile("US Letter — 8.5 × 11 in", "LETTER");
         AddProfile("A4 — 210 × 297 mm", "A4");
         AddProfile("4 × 6 in label", "LABEL_4X6_300");
      }
      string preferred = TestPrintDefaults.Profile(SelectedFormat, _driverName);
      _profile.SelectedItem = _profile.Items.Cast<ComboBoxItem>().First(item => string.Equals(item.Tag as string, preferred, StringComparison.Ordinal));
      _profile.Header = zpl ? "Label stock and resolution" : "Paper or label stock";
      _guidance.Text = zpl
         ? "Load 4 × 6 in labels and verify the printer's actual resolution before printing. The default is only a driver-name suggestion (203 dpi when unknown); a wrong DPI changes the printed size. This diagnostic does not calibrate the printer or save device configuration."
         : "Load stock matching the selected size and set the Windows queue to the same paper or label size. The diagnostic prints one copy at actual size; the stock selection does not change Windows printer defaults.";
   }

   private void AddProfile(string label, string value) => _profile.Items.Add(new ComboBoxItem { Content = label, Tag = value });
}
