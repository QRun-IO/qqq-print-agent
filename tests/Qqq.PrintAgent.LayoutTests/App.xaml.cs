/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using KofTwentyTwo.AppKit.Logging;
using Microsoft.UI.Xaml;

namespace Qqq.PrintAgent;

/// <summary>The layout harness's application object: it opens the real main window over the non-printing synthetic service, runs the layout scenario, and turns any unhandled UI error or scenario failure into a failing exit code with evidence written to disk.</summary>
public partial class App : Application
{
   private Window? _window;
   internal static IActivityLog Log { get; } = AppIdentity.CreateLog();



   /// <summary>Initializes the XAML, turns on layout-cycle tracing, and records any unhandled UI exception as unhandled.txt with exit code 1 instead of letting a layout failure pass silently as a success.</summary>
   public App()
   {
      InitializeComponent();
      DebugSettings.LayoutCycleTracingLevel = LayoutCycleTracingLevel.High;
      UnhandledException += (_, args) =>
      {
         // A real native layout failure is a failing result, never a swallowed test success.
         File.WriteAllText(Path.Combine(Program.EvidenceDirectory, "unhandled.txt"), args.Exception.ToString());
         Environment.ExitCode = 1;
         args.Handled = true;
         (_window as IDisposable)?.Dispose();
         Exit();
      };
   }



   /// <summary>Opens the main window at the requested size wired to the synthetic service and runs the scenario. Success exits 0; any exception is written to failure.txt and exits 1. Either way the window is disposed and the app exits.</summary>
   protected override async void OnLaunched(LaunchActivatedEventArgs args)
   {
      var service = new SyntheticService();
      try
      {
         var mainWindow = new MainWindow(service.SendAsync);
         _window = mainWindow;
         mainWindow.AppWindow.Resize(new global::Windows.Graphics.SizeInt32(Program.WindowWidth, Program.WindowHeight));
         _window.Activate();
         await LayoutScenario.RunAsync(mainWindow, service);
         Environment.ExitCode = 0;
      }
      catch(Exception exception)
      {
         File.WriteAllText(Path.Combine(Program.EvidenceDirectory, "failure.txt"), exception.ToString());
         Environment.ExitCode = 1;
      }
      finally
      {
         (_window as IDisposable)?.Dispose();
         Exit();
      }
   }
}
