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
      ContentDialog dialog = CreateWideDialog(title, editor);
      dialog.PrimaryButtonText = "Save";
      dialog.CloseButtonText = "Cancel";
      dialog.DefaultButton = ContentDialogButton.Primary;
      return await DialogGuard.ShowAsync(dialog) == ContentDialogResult.Primary;
   }

   private void PrinterSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateSelectedPrinter();

   private void TogglePrinterDetails(PrinterRowView row)
      => PrintersList.SelectedItem = ReferenceEquals(PrintersList.SelectedItem, row) ? null : row;

   private void UpdateSelectedPrinter()
   {
      var selected = PrintersList.SelectedItem as PrinterRowView;
      foreach(PrinterRowView row in _printerRows.Values)
      {
         row.SetSelected(ReferenceEquals(row, selected));
         row.SetBusy(_busy);
      }
   }

   private async Task EditPrinterAsync(PrinterSnapshot printer)
   {
      var editor = new ProfileEditor(printer.DisplayName, printer.Description, printer.Location);
      editor.NameBox.PlaceholderText = printer.Name;
      editor.AddPrinterContext(printer.Name, FriendlyState(printer.Status));
      if(await EditProfileAsync("Edit printer details", editor))
      {
         await RunActionAsync(new PipeRequest("set-printer-profile", PrinterId: printer.PrinterId, PrinterProfile: new PrinterProfile(editor.NameBox.Text, editor.DescriptionBox.Text, editor.LocationBox.Text)));
      }
   }

   private static string PrinterLabel(PrinterSnapshot printer) => string.IsNullOrWhiteSpace(printer.DisplayName) ? printer.Name : printer.DisplayName;
}
