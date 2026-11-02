/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;


namespace Qqq.PrintAgent;

/// <summary>
/// Menu part of the main window: the File and Help menu commands. Help links open in the user's browser, and when the browser
/// cannot be launched the address is shown so it can still be used.
/// </summary>
public sealed partial class MainWindow
{
   /// <summary>Opens the Settings page from the File menu.</summary>
   private void SettingsMenu_Click(object sender, RoutedEventArgs e)
      => Navigation.SelectedItem = Navigation.SettingsItem;



   /// <summary>Closes the companion window from the File menu; the window's close handling decides whether it hides to the tray or exits.</summary>
   private void CloseDetailsMenu_Click(object sender, RoutedEventArgs e) => Close();



   /// <summary>Opens the address carried in a Help menu item's tag in the browser.</summary>
   private async void HelpLinkMenu_Click(object sender, RoutedEventArgs e)
   {
      if(sender is MenuFlyoutItem { Tag: string address })
      {
         await OpenHelpLinkAsync(new Uri(address));
      }
   }



   /// <summary>Opens the support address, which is the customer's own link when branding supplies one and otherwise the project's issue tracker.</summary>
   private async void SupportMenu_Click(object sender, RoutedEventArgs e)
      => await OpenHelpLinkAsync(SupportLink.NavigateUri);



   /// <summary>Launches an address in the default browser and, when Windows refuses or fails to launch it, shows the address as a warning so the operator can paste it into a browser themselves.</summary>
   private async Task OpenHelpLinkAsync(Uri address)
   {
      try
      {
         if(!await global::Windows.System.Launcher.LaunchUriAsync(address))
         {
            Feedback("Could not open your browser", "Open this address in your browser: " + address, InfoBarSeverity.Warning);
         }
      }
      catch(Exception exception) when(exception is COMException or UnauthorizedAccessException or InvalidOperationException)
      {
         Feedback("Could not open your browser", "Open this address in your browser: " + address, InfoBarSeverity.Warning);
      }
   }
}
