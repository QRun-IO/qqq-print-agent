/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using KofTwentyTwo.AppKit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

/// <summary>
/// Names part of the main window: the operator's own labels for this computer and each printer. Edits are saved through the
/// service, which validates and synchronizes them to QQQ; the window only shows what the service returns.
/// </summary>
public sealed partial class MainWindow
{
   private DeviceProfile _deviceProfile = new();



   /// <summary>Shows the computer's name, location and description from the latest snapshot, using the Windows computer name when the operator has not set one.</summary>
   private void UpdateDeviceProfile(AgentSnapshot snapshot)
   {
      _deviceProfile = snapshot.DeviceProfile ?? new DeviceProfile();
      string name = string.IsNullOrWhiteSpace(_deviceProfile.Name) ? Environment.MachineName : _deviceProfile.Name;
      DeviceProfileText.Text = $"{name}\nLocation: {_deviceProfile.Location ?? "Not set"}\n{_deviceProfile.Description}";
   }



   /// <summary>Opens the computer-details editor and, when the operator saves, sends the new values to the service; the Windows computer name is the placeholder so an empty name is clearly the default.</summary>
   private async void EditDevice_Click(object sender, RoutedEventArgs e)
   {
      var editor = new ProfileEditor(_deviceProfile.Name, _deviceProfile.Description, _deviceProfile.Location);
      editor.NameBox.PlaceholderText = Environment.MachineName;
      if(await EditProfileAsync("Edit computer details", editor))
      {
         await RunActionAsync(new PipeRequest("set-device-profile", DeviceProfile: new DeviceProfile(editor.NameBox.Text, editor.DescriptionBox.Text, editor.LocationBox.Text)));
      }
   }



   /// <summary>Shows a profile editor in a wide dialog with Save as the default and Cancel to leave; it returns true only when the operator chose Save, and never opens while another request is running.</summary>
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



   /// <summary>Re-applies expansion and enabled state to every printer row whenever the selection changes.</summary>
   private void PrinterSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateSelectedPrinter();



   /// <summary>Expands the clicked printer row, or collapses it when it is already the selected one, so the disclosure button toggles.</summary>
   private void TogglePrinterDetails(PrinterRowView row)
      => PrintersList.SelectedItem = ReferenceEquals(PrintersList.SelectedItem, row) ? null : row;



   /// <summary>Marks the selected row expanded and every other collapsed, and gives each row the current busy state so its buttons match what the window allows.</summary>
   private void UpdateSelectedPrinter()
   {
      var selected = PrintersList.SelectedItem as PrinterRowView;
      foreach(PrinterRowView row in _printerRows.Values)
      {
         row.SetSelected(ReferenceEquals(row, selected));
         row.SetBusy(_busy);
      }
   }



   /// <summary>Opens the printer-details editor with the Windows queue name and current state shown for context, and sends the edited display name, description and location to the service when saved.</summary>
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



   /// <summary>The name to show for a printer: the operator's friendly name when set, otherwise the Windows queue name.</summary>
   private static string PrinterLabel(PrinterSnapshot printer) => string.IsNullOrWhiteSpace(printer.DisplayName) ? printer.Name : printer.DisplayName;
}
