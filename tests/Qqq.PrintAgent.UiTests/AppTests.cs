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
      session.InvokeMenuItem("Help", "AboutMenuItem");
      AutomationElement version = session.WaitForElement("AboutVersionText");
      Assert.StartsWith("Version ", version.Name, StringComparison.Ordinal);
      session.ClickButton("Close");
      session.WaitForElementGone("AboutVersionText");
   }



   /// <summary>The setup screen exposes enrollment, status and inventory controls.</summary>
   [Fact]
   public void Setup_ShowsConnectionAndStatusControls()
   {
      using var session = new AppSession();
      session.ClickElement("ConnectionSettings");
      session.WaitForElement("ServerAddress");
      session.WaitForElement("EnrollmentCode");
      Assert.Equal("Connect this device", session.WaitForElement("EnrollDevice").Name);
      session.ClickButton("Close");
      session.WaitForElement("ServiceStatus");
      session.WaitForElement("AgentHealth");
      session.WaitForElement("FooterStatus");
      session.WaitForElement("ViewServiceLogs");
      session.WaitForElement("PrinterInventory");
      session.WaitForElement("RecentJobs");
      session.WaitForElement("RefreshStatus");
      Assert.False(session.App.HasExited);
   }



   /// <summary>The close button hides details while the tray process stays alive.</summary>
   [Fact]
   public void Close_HidesDetailsAndKeepsTrayAlive()
   {
      using var session = new AppSession();
      session.WaitForElement("AgentHealth");
      session.MainWindow.Close();
      session.WaitFor(() => session.MainWindow.IsOffscreen ? session.MainWindow : null, "details window to hide to the tray");
      Assert.False(session.App.HasExited);
   }



   /// <summary>Activity log: opens a window showing the log.</summary>
   [Fact]
   public void ActivityLog_OpensAWindowShowingTheLog()
   {
      using var session = new AppSession();
      session.InvokeMenuItem("Help", "ActivityLogMenuItem");
      string title = AppIdentity.Info.DisplayName + " — Activity log";
      session.WaitFor(
         () => session.TopLevelWindows().FirstOrDefault(window => string.Equals(window.Name, title, StringComparison.Ordinal)),
         "the activity log window");
   }



   /// <summary>Theme: the choice is saved to settings.json.</summary>
   [Fact]
   public void ThemeChoice_IsSaved()
   {
      using var session = new AppSession();
      session.InvokeMenuItem("View", "ThemeDark");
      session.WaitFor(
         () => session.ReadSettings().Contains("\"Theme\": \"Dark\"", StringComparison.Ordinal) ? session.MainWindow : null,
         "settings.json to record the dark theme");
   }



   /// <summary>Invalid connection details are rejected locally before enrollment.</summary>
   [Fact]
   public void InvalidConnection_ShowsActionableValidation()
   {
      using var session = new AppSession();
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
