using KofTwentyTwo.AppKit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

public sealed partial class MainWindow
{
   private DeviceProfile _deviceProfile = new();

   private void UpdateDeviceProfile(AgentSnapshot snapshot)
   {
      _deviceProfile = snapshot.DeviceProfile ?? new DeviceProfile();
      string name = string.IsNullOrWhiteSpace(_deviceProfile.Name) ? Environment.MachineName : _deviceProfile.Name;
      DeviceProfileText.Text = $"{name}\nLocation: {_deviceProfile.Location ?? "Not set"}\n{_deviceProfile.Description}";
   }

   private async void EditDevice_Click(object sender, RoutedEventArgs e)
   {
      var editor = new ProfileEditor(_deviceProfile.Name, _deviceProfile.Description, _deviceProfile.Location);
      editor.NameBox.PlaceholderText = Environment.MachineName;
      if(await EditProfileAsync("Edit computer details", editor))
      {
         await RunActionAsync(new PipeRequest("set-device-profile", DeviceProfile: new DeviceProfile(editor.NameBox.Text, editor.DescriptionBox.Text, editor.LocationBox.Text)));
      }
   }

   private async Task<bool> EditProfileAsync(string title, ProfileEditor editor)
   {
      if(_busy)
      {
         return false;
      }
      var dialog = new ContentDialog { Title = title, Content = new ScrollViewer { Content = editor, MaxHeight = 420 }, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot };
      return await DialogGuard.ShowAsync(dialog) == ContentDialogResult.Primary;
   }

   private void PrinterSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateSelectedPrinter();

   private void UpdateSelectedPrinter()
   {
      PrinterSnapshot? printer = (PrintersList.SelectedItem as PrinterRowView)?.Snapshot;
      PrinterDetailsButton.IsEnabled = printer is not null && !_busy;
      PrinterEditButton.IsEnabled = printer is not null && !_busy;
      PrinterTestButton.IsEnabled = !_busy && printer is { Exposed: true } && printer.Formats.Length > 0;
      SelectedPrinterText.Text = printer is null ? "Select a printer to view or edit its details." : $"{PrinterLabel(printer)}\nWindows queue: {printer.Name}";
      ToolTipService.SetToolTip(SelectedPrinterText, SelectedPrinterText.Text);
   }

   private async void PrinterTest_Click(object sender, RoutedEventArgs e)
   {
      if((PrintersList.SelectedItem as PrinterRowView)?.Snapshot is PrinterSnapshot printer)
      {
         await TestPrintAsync(printer);
      }
   }

   private async void PrinterEdit_Click(object sender, RoutedEventArgs e)
   {
      if((PrintersList.SelectedItem as PrinterRowView)?.Snapshot is not PrinterSnapshot printer)
      {
         return;
      }
      var editor = new ProfileEditor(printer.DisplayName, printer.Description, printer.Location);
      editor.NameBox.PlaceholderText = printer.Name;
      if(await EditProfileAsync("Edit printer details", editor))
      {
         await RunActionAsync(new PipeRequest("set-printer-profile", PrinterId: printer.PrinterId, PrinterProfile: new PrinterProfile(editor.NameBox.Text, editor.DescriptionBox.Text, editor.LocationBox.Text)));
      }
   }

   private async void PrinterDetails_Click(object sender, RoutedEventArgs e)
   {
      if(_busy || (PrintersList.SelectedItem as PrinterRowView)?.Snapshot is not PrinterSnapshot printer)
      {
         return;
      }
      string text = $"Name: {PrinterLabel(printer)}\nWindows queue: {printer.Name}\nLocation: {printer.Location}\nDescription: {printer.Description}\n\nStatus: {FriendlyState(printer.Status)}\nFormats: {string.Join(", ", printer.Formats)}\nDriver: {printer.DriverName}\nPort: {printer.PortName}\nPrinter ID: {printer.PrinterId}";
      var dialog = new ContentDialog { Title = "Printer details", CloseButtonText = "Close", XamlRoot = Content.XamlRoot, Content = new ScrollViewer { MaxHeight = 480, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } } };
      await DialogGuard.ShowAsync(dialog);
   }

   private static string PrinterLabel(PrinterSnapshot printer) => string.IsNullOrWhiteSpace(printer.DisplayName) ? printer.Name : printer.DisplayName;
}
