/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using KofTwentyTwo.AppKit.Logging;
using KofTwentyTwo.AppKit.WinUI;
using Microsoft.UI.Xaml;
using Qqq.PrintAgent.Windows;


namespace Qqq.PrintAgent;

/// <summary>The application: installs AppKit's crash net and the saved theme, then opens the main window.</summary>
public partial class App : Application
{
   private Window? _window;



   /// <summary>Installs the crash net and applies the saved theme; WinUI allows setting the application theme only here, before any window exists.</summary>
   public App()
   {
      InitializeComponent();
      WinUIShell.InstallCrashNet(this, () => Log);
      UnhandledException += UiExceptionPolicy.HandleUnhandledException;
      WinUIShell.ApplyTheme(this, AppIdentity.SettingsStore.Load().ThemeKind);
   }



   /// <summary>The app-wide activity log.</summary>
   internal static IActivityLog Log { get; } = AgentActivityLog.Create(AgentStore.DefaultDirectory);



   /// <summary>Logs the start and opens the main window.</summary>
   protected override void OnLaunched(LaunchActivatedEventArgs args)
   {
      Log.Info($"[Companion] {AppIdentity.Info.DisplayName} started.");
      // MainWindow disposes its tray and activation handles from its Closed event.
      var mainWindow = new MainWindow();
      _window = mainWindow;
      _window.Activate();
      mainWindow.ApplyStartupVisibility();
   }
}
