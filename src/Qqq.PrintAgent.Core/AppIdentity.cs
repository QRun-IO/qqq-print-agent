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
/// A compiled-in client <see cref="Edition"/> changes names, colours and the update asset; machine
/// branding applies on top of that; the id and paths never change.
/// </summary>
public static class AppIdentity
{
   /// <summary>The generic identity before any edition or machine branding: id, names, links, credits, and brand.</summary>
   public static AppInfo BaseInfo { get; } = new()
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
         new("KofTwentyTwo.AppKit / AppKit.WinUI / AppKit.Updates", "MIT License"),
         new("CommunityToolkit.Mvvm", "MIT License"),
         new("H.NotifyIcon", "MIT License"),
         new("Microsoft.Data.Sqlite", "MIT License"),
         new("SQLitePCLRaw", "Apache License 2.0"),
         new("Velopack", "MIT License"),
         new("Windows App SDK / WinUI 3", "Microsoft license terms; see ThirdPartyNotices in the installed App directory"),
         new(".NET Runtime", "MIT License"),
      ],
   };



   /// <summary>The client edition compiled into this build, or null for the generic build.</summary>
   public static Edition? Edition { get; } = Edition.LoadInstalled();

   /// <summary>The identity this build ships with: the edition's presentation over <see cref="BaseInfo"/>, before machine branding.</summary>
   public static AppInfo DefaultInfo { get; } = Edition?.Apply(BaseInfo) ?? BaseInfo;

   /// <summary>The release asset prefix the installed updater looks for, so an edition keeps updating on its own line.</summary>
   public static string UpdateAssetPrefix => Edition?.AssetPrefix ?? Core.Edition.DefaultAssetPrefix;

   /// <summary>The edition's logo shipped beside the executable, when the edition supplies one and no machine branding logo overrides it.</summary>
   public static string? EditionLogoPath
   {
      get
      {
         if(Edition is null)
         {
            return null;
         }
         string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", "logo.png");
         return File.Exists(path) ? path : null;
      }
   }

   public static RuntimeBranding Branding { get; } = RuntimeBranding.LoadMachine();
   public static AppInfo Info { get; } = Branding.Apply(DefaultInfo);

   /// <summary>Where the app keeps its per-user data; honors the data-folder override variable.</summary>
   public static AppPaths Paths { get; } = new(Info);



   /// <summary>Loads and saves <see cref="AppSettings"/> at <see cref="AppPaths.SettingsFile"/>.</summary>
   public static SettingsStore<AppSettings> SettingsStore { get; } = new(Paths.SettingsFile, AppSettingsContext.Default.AppSettings);



   /// <summary>Creates the app's daily activity log under <see cref="AppPaths.LogsDirectory"/>.</summary>
   public static IActivityLog CreateLog() => FileActivityLog.ForApp(Info, Paths);
}
