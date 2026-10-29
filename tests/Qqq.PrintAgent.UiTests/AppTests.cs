/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using FlaUI.Core.AutomationElements;


namespace Qqq.PrintAgent.UiTests;

/// <summary>
/// End-to-end tests of the print setup screen and AppKit shell.
/// Run on an elevated interactive desktop; tests never enroll a device or submit print jobs.
/// </summary>
[Collection("Desktop")]
public class AppTests
{
   /// <summary>Splash: shows, then dismisses itself.</summary>
   [Fact]
   public void Splash_ShowsThenDismissesItself()
   {
      using var session = new AppSession(showSplash: true);
      session.WaitForElement("AppKitSplash");
      session.WaitForElementGone("AppKitSplash", TimeSpan.FromSeconds(15));
   }



   /// <summary>About: shows the build version.</summary>
   [Fact]
   public void About_ShowsTheBuildVersion()
   {
      using var session = new AppSession();
      session.ClickElement("NavigateSettings");
      session.ClickElement("AboutMenuItem");
      AutomationElement version = session.WaitForElement("AboutVersionText");
      Assert.StartsWith("Version ", version.Name, StringComparison.Ordinal);
      session.ClickButton("Close");
      session.WaitForElementGone("AboutVersionText");
   }



   /// <summary>Native navigation separates printers, jobs and settings while status remains visible.</summary>
   [Fact]
   public void Setup_ShowsConnectionAndStatusControls()
   {
      using var session = new AppSession();
      session.WaitForElement("PrinterInventory");
      session.WaitForElement("FooterStatus");
      session.WaitForElement("ViewServiceLogs");
      session.ClickElement("NavigateJobs");
      session.WaitForElement("RecentJobs");
      session.WaitForElement("FooterStatus");
      session.ClickElement("NavigateSettings");
      session.ClickElement("ConnectionSettings");
      session.WaitForElement("ServerAddress");
      session.WaitForElement("EnrollmentCode");
      Assert.Equal("Connect this device", session.WaitForElement("EnrollDevice").Name);
      session.ClickButton("Close");
      session.WaitForElement("ServiceStatus");
      session.WaitForElement("FooterStatus");
      session.ClickElement("NavigatePrinters");
      session.WaitForElement("PrinterInventory");
      session.WaitForElement("RefreshStatus");
      Assert.False(session.App.HasExited);
   }



   /// <summary>The close button hides details while the tray process stays alive.</summary>
   [Fact]
   public void Close_HidesDetailsAndKeepsTrayAlive()
   {
      using var session = new AppSession();
      session.WaitForElement("FooterStatus");
      session.MainWindow.Close();
      session.WaitFor(() => session.MainWindow.IsOffscreen ? session.MainWindow : null, "details window to hide to the tray");
      Assert.False(session.App.HasExited);
   }

   /// <summary>A second launch restores the hidden existing window and exits without another companion.</summary>
   [Fact]
   public async Task Relaunch_RestoresHiddenExistingCompanion()
   {
      using var session = new AppSession();
      session.WaitForElement("FooterStatus");
      int originalProcess = session.App.ProcessId;
      session.MainWindow.Close();
      session.WaitFor(() => session.MainWindow.IsOffscreen ? session.MainWindow : null, "original window to hide");
      using System.Diagnostics.Process second = session.Relaunch();
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
      await second.WaitForExitAsync(timeout.Token);
      Assert.Equal(0, second.ExitCode);
      session.WaitFor(() => !session.MainWindow.IsOffscreen ? session.MainWindow : null, "original window to reappear");
      Assert.False(session.App.HasExited);
      Assert.Equal(originalProcess, session.App.ProcessId);
      Assert.Single(session.TopLevelWindows(), window => string.Equals(window.Name, session.MainWindow.Name, StringComparison.Ordinal));
   }

   /// <summary>Search/filter remain read-only and lifecycle controls are discoverable without invoking them.</summary>
   [Fact]
   public void JobSearchAndLifecycleControls_AreUsableWithoutMutation()
   {
      using var session = new AppSession();
      session.ClickElement("NavigateJobs");
      session.EnterText("JobSearch", "no-match-" + Guid.NewGuid().ToString("N"));
      session.WaitForElement("JobStatusFilter").AsComboBox().Select("Needs attention");
      session.WaitFor(() => session.WaitForElement("JobsSummary").Name.StartsWith("0 of ", StringComparison.Ordinal) ? session.MainWindow : null, "empty filtered job result");
      session.WaitForElement("FooterStatus");
      session.ClickElement("NavigateSettings");
      Assert.False(session.WaitForElement("StartService").IsOffscreen);
      Assert.False(session.WaitForElement("RestartService").IsOffscreen);
      session.WaitForElement("StartupAtSignIn");
      session.WaitForElement("ConnectionSettings");
      Assert.False(session.App.HasExited);
   }



   /// <summary>Logs navigation and the footer reuse the same built-in AppKit viewer.</summary>
   [Fact]
   public void ActivityLog_OpensAWindowShowingTheLog()
   {
      using var session = new AppSession();
      session.ClickElement("NavigateLogs");
      string title = AppIdentity.Info.DisplayName + " — Activity log";
      session.WaitFor(
         () => session.TopLevelWindows().FirstOrDefault(window => string.Equals(window.Name, title, StringComparison.Ordinal)),
         "the activity log window");
      session.WaitForElement("LogText");
      session.MainWindow.SetForeground();
      session.ClickElement("ViewServiceLogs");
      Assert.Single(session.TopLevelWindows(), window => string.Equals(window.Name, title, StringComparison.Ordinal));
   }



   /// <summary>Theme: the choice is saved to settings.json.</summary>
   [Fact]
   public void ThemeChoice_IsSaved()
   {
      using var session = new AppSession();
      session.ClickElement("NavigateSettings");
      session.WaitForElement("AppTheme").AsComboBox().Select("Dark");
      session.WaitFor(
         () => session.ReadSettings().Contains("\"Theme\": \"Dark\"", StringComparison.Ordinal) ? session.MainWindow : null,
         "settings.json to record the dark theme");
   }



   /// <summary>Invalid connection details are rejected locally before enrollment.</summary>
   [Fact]
   public void InvalidConnection_ShowsActionableValidation()
   {
      using var session = new AppSession();
      session.ClickElement("NavigateSettings");
      session.ClickElement("ConnectionSettings");
      session.EnterText("ServerAddress", "not-a-server-address");
      session.WaitFor(
         () => session.FindInApp(cf => cf.ByAutomationId("EnrollDevice")) is { IsEnabled: true } button ? button : null,
         "initial status refresh to finish", TimeSpan.FromSeconds(45));
      session.ClickElement("EnrollDevice");
      session.WaitFor(
         () => session.FindInApp(cf => cf.ByName("Check the connection details")),
         "connection validation guidance");
      Assert.False(session.App.HasExited);
   }
}



/// <summary>UI tests share one desktop, so the collection never runs in parallel.</summary>
[CollectionDefinition("Desktop", DisableParallelization = true)]
public class DesktopCollectionDefinition
{
}
