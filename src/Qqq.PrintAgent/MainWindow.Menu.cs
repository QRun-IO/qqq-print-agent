/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;


namespace Qqq.PrintAgent;

public sealed partial class MainWindow
{
   private void SettingsMenu_Click(object sender, RoutedEventArgs e)
      => Navigation.SelectedItem = Navigation.SettingsItem;

   private void CloseDetailsMenu_Click(object sender, RoutedEventArgs e) => Close();

   private async void HelpLinkMenu_Click(object sender, RoutedEventArgs e)
   {
      if(sender is MenuFlyoutItem { Tag: string address })
      {
         await OpenHelpLinkAsync(new Uri(address));
      }
   }

   private async void SupportMenu_Click(object sender, RoutedEventArgs e)
      => await OpenHelpLinkAsync(SupportLink.NavigateUri);

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
