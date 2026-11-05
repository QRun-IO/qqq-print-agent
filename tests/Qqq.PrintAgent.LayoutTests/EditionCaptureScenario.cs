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
   internal static async Task RunAsync(FrameworkElement root)
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
