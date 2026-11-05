/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using KofTwentyTwo.AppKit;
using KofTwentyTwo.AppKit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Qqq.PrintAgent;

/// <summary>
/// Optional visual evidence of the build's identity (generic or a client edition): the splash as it opens, the main window,
/// the Settings page with the logo and the shared About dialog in both themes, captured from the real views. It runs only
/// when QQQ_LAYOUT_CAPTURE_EDITION=1 is set, so the normal layout gate is unchanged.
/// </summary>
internal static class EditionCaptureScenario
{
   /// <summary>Shows the connection dialog with a server address and the browser button visible, captures it, and hides it again.</summary>
   private static async Task CaptureConnectionDialogAsync(MainWindow window, FrameworkElement root)
   {
      // A dialog that was shown once lives in a popup afterwards, so take the window's own fields rather than a name lookup.
      ContentDialog connection = Field<ContentDialog>(window, "ConnectionDialog");
      connection.XamlRoot = root.XamlRoot;
      Field<TextBox>(window, "ServerBox").Text = "https://127.0.0.1:18581";
      Field<Button>(window, "BrowserSignInButton").Visibility = Visibility.Visible;
      Task<ContentDialogResult?> connecting = DialogGuard.ShowAsync(connection);
      try
      {
         // The dialog is measured inside its popup after it opens; render only once it has a real size, and capture
         // its scroll content (the dialog chrome itself is a popup layer that RenderTargetBitmap does not accept).
         for(int waited = 0; waited < 40 && (connection.ActualWidth < 1 || connection.ActualHeight < 1); waited++)
         { await Task.Delay(100); }
         await Task.Delay(500);
         connection.UpdateLayout();
         // Captured on its own the content has no backdrop, so give it the light theme and a solid page colour for the picture.
         connection.RequestedTheme = ElementTheme.Light;
         // The dialog's content is the feedback bar over the scrolling form; capture that whole grid.
         var content = (Grid)connection.Content;
         content.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
         content.Padding = new Thickness(24, 24, 40, 24);
         await Task.Delay(300);
         content.UpdateLayout();
         await QueryAboutScenario.CaptureAsync(content, "edition-connection-dialog.png", requireContrast: false);
      }
      finally
      {
         connection.Hide();
         await connecting;
         Field<Button>(window, "BrowserSignInButton").Visibility = Visibility.Collapsed;
      }
   }



   /// <summary>Reads one of the window's generated XAML fields, which stay valid after a dialog has been reparented into a popup.</summary>
   private static T Field<T>(MainWindow window, string name) where T : class
      => typeof(MainWindow).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(window) as T
         ?? throw new InvalidOperationException("Window field missing: " + name);



   /// <summary>True when the caller asked for identity captures.</summary>
   internal static bool IsEnabled => string.Equals(Environment.GetEnvironmentVariable("QQQ_LAYOUT_CAPTURE_EDITION"), "1", StringComparison.Ordinal);



   /// <summary>Captures the window while the splash overlay is still up, which the harness enables only in capture mode.</summary>
   internal static async Task CaptureSplashAsync(FrameworkElement root)
   {
      if(!IsEnabled)
      {
         return;
      }
      root.UpdateLayout();
      await QueryAboutScenario.CaptureAsync(root, "edition-splash.png", requireContrast: false);
   }



   /// <summary>Captures the Printers page, the Settings page and the About dialog (light and dark) with the build's real identity, then returns to Printers.</summary>
   internal static async Task RunAsync(MainWindow window, FrameworkElement root)
   {
      if(!IsEnabled)
      {
         return;
      }
      await Task.Delay(500);
      root.UpdateLayout();
      await QueryAboutScenario.CaptureAsync(root, "edition-window.png", requireContrast: false);
      var navigation = (NavigationView)root.FindName("Navigation");
      navigation.SelectedItem = navigation.SettingsItem;
      await Task.Delay(600);
      root.UpdateLayout();
      await QueryAboutScenario.CaptureAsync(root, "edition-settings.png", requireContrast: false);
      foreach(ElementTheme theme in new[] { ElementTheme.Light, ElementTheme.Dark })
      {
         AppInfo identity = AppIdentity.Info;
         var dialog = new AboutDialog(identity) { XamlRoot = root.XamlRoot, RequestedTheme = theme };
         AboutContent.Compose((StackPanel)((ScrollViewer)dialog.Content).Content, AppIdentity.Branding.SupportUrl, null);
         Task<ContentDialogResult?> showing = DialogGuard.ShowAsync(dialog);
         try
         {
            await Task.Delay(700);
            dialog.UpdateLayout();
            await QueryAboutScenario.CaptureAsync(dialog, $"edition-about-{theme}.png", requireContrast: false);
         }
         finally
         {
            dialog.Hide();
            await showing;
         }
      }
      navigation.SelectedItem = navigation.MenuItems.OfType<NavigationViewItem>().First();
      await Task.Delay(300);
      await CaptureConnectionDialogAsync(window, root);
      File.WriteAllText(Path.Combine(Program.EvidenceDirectory, "edition-identity.json"), System.Text.Json.JsonSerializer.Serialize(new
      {
         edition = AppIdentity.Edition?.Id,
         displayName = AppIdentity.Info.DisplayName,
         tagline = AppIdentity.Info.Tagline,
         company = AppIdentity.Edition?.CompanyName,
         accent = AppIdentity.Info.Brand.GradientStart,
         updateAssetPrefix = AppIdentity.UpdateAssetPrefix,
         logo = AppIdentity.EditionLogoPath,
      }));
   }
}
