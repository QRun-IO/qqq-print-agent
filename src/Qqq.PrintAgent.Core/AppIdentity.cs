/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using KofTwentyTwo.AppKit;
using KofTwentyTwo.AppKit.Logging;
using KofTwentyTwo.AppKit.Settings;


namespace Qqq.PrintAgent.Core;

/// <summary>
/// The application's identity, declared once and shared by the window, the command line,
/// and the tests. AppKit derives the data folder (%LOCALAPPDATA%\qqq-print-agent), the data-folder
/// override variable, the log file names, and the credential prefix from <see cref="Info"/>.
/// </summary>
public static class AppIdentity
{
   /// <summary>Who the app is: id, names, links, credits, and brand.</summary>
   public static AppInfo Info { get; } = new()
   {
      Id = "qqq-print-agent",
      DisplayName = "QQQ Print Agent",
      Tagline = "Cloud printing for QQQ",
      Description = "Connect local printers to QQQ.",
      RepositoryUrl = new Uri("https://github.com/QRun-IO/qqq-print-agent"),
      Author = "James Maes",
      Copyright = "Copyright (c) 2026 James Maes",
      // Keep the gradient in step with the mark (regenerate both with AppKit's BrandTool).
      Brand = new AppBrand
      {
         GradientStart = "#0F766E",
         GradientEnd = "#134E4A",
         IconSvgPath = "Assets/Brand/app-icon.svg",
         IconIcoPath = "Assets/Brand/app.ico",
      },
      Attributions =
      [
         new("KofTwentyTwo.AppKit", "MIT License"),
         new("CommunityToolkit.Mvvm", "MIT License"),
         new("Velopack", "MIT License"),
         new("Windows App SDK / WinUI 3", "MIT License, with Microsoft binary components"),
         new(".NET Runtime", "MIT License"),
      ],
   };



   /// <summary>Where the app keeps its per-user data; honors the data-folder override variable.</summary>
   public static AppPaths Paths { get; } = new(Info);



   /// <summary>Loads and saves <see cref="AppSettings"/> at <see cref="AppPaths.SettingsFile"/>.</summary>
   public static SettingsStore<AppSettings> SettingsStore { get; } = new(Paths.SettingsFile, AppSettingsContext.Default.AppSettings);



   /// <summary>Creates the app's daily activity log under <see cref="AppPaths.LogsDirectory"/>.</summary>
   public static IActivityLog CreateLog() => FileActivityLog.ForApp(Info, Paths);
}


