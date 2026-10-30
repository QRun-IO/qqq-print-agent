/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;


namespace Qqq.PrintAgent.UiTests;

/// <summary>
/// End-to-end tests of the print setup screen and AppKit shell.
/// Run on an elevated interactive desktop; tests never enroll a device or submit print jobs.
/// </summary>
[Collection("Desktop")]
public class AppTests
{
   /// <summary>File and Help expose the same native settings/About flows without starting updates.</summary>
   [Fact]
   public void MenuBar_ProvidesStandardAppKitCommands()
   {
      using var session = new AppSession();
      session.WaitForElement("AppMenuBar");
      session.InvokeMenuItem("File", "SettingsMenuItem");
      session.WaitForElement("ServiceStatus");
      AutomationElement updates = session.OpenMenuAndFindItem("Help", "CheckUpdatesMenuItem");
      Assert.Equal("Check for updates", updates.Name);
      session.WaitForElement("ActivityLogHelpMenuItem");
      session.WaitForElement("UserGuideMenuItem");
      session.WaitForElement("SupportMenuItem");
      session.WaitForElement("ReleaseNotesMenuItem");
      session.ClickElement("AboutHelpMenuItem");
      Assert.StartsWith("Version ", session.WaitForElement("AboutVersionText").Name, StringComparison.Ordinal);
      session.ClickButton("Close");
      session.WaitForElementGone("AboutVersionText");
   }

   /// <summary>Splash: shows, then dismisses itself.</summary>
   [Fact]
   public void Splash_ShowsThenDismissesItself()
   {
      using var session = new AppSession(showSplash: true);
      session.WaitForElement("AppKitSplash");
      session.WaitForElementGone("AppKitSplash", TimeSpan.FromSeconds(20));
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
      string[] jobHeaders = ["SortJobTitle", "SortJobPrinter", "SortJobStatus", "SortJobFormat", "SortJobCreated"];
      foreach(string header in jobHeaders)
      {
         AutomationElement button = session.WaitForElement(header);
         Assert.Equal(ControlType.Button, button.ControlType);
         Assert.False(button.IsOffscreen);
      }
      session.WaitForElement("PreviousJobPage");
      session.WaitForElement("NextJobPage");
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

   /// <summary>The computer metadata dialog permits inspection and cancellation without changing the service.</summary>
   [Fact]
   public void MetadataEditor_CanBeOpenedAndCanceled()
   {
      using var session = new AppSession();
      string[] printerHeaders = ["SortPrinterName", "SortPrinterStatus", "SortPrinterLocation", "SortPrinterFormats", "SortPrinterShared"];
      foreach(string header in printerHeaders)
      {
         AutomationElement button = session.WaitForElement(header);
         Assert.Equal(ControlType.Button, button.ControlType);
         Assert.False(button.IsOffscreen);
      }
      session.ClickElement("NavigateSettings");
      session.ClickElement("EditDeviceProfile");
      session.WaitForElement("MetadataName");
      session.WaitForElement("MetadataLocation");
      session.WaitForElement("MetadataDescription");
      session.ClickButton("Cancel");
      session.WaitForElementGone("MetadataName");
      Assert.False(session.App.HasExited);
   }

   /// <summary>A real installed queue expands in place and opens settings without saving or printing.</summary>
   [Fact]
   public void ExistingPrinter_DoubleClickOpensSettingsWithoutMutation()
   {
      using var session = new AppSession();
      AutomationElement inventory = session.WaitForElement("PrinterInventory");
      AutomationElement row = session.WaitFor(() => inventory.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)), "an installed printer row");
      row.Patterns.SelectionItem.Pattern.Select();
      AutomationElement settings = session.WaitForElement("EditPrinterProfile");
      Assert.False(settings.IsOffscreen);
      Assert.True(settings.BoundingRectangle.Top >= row.BoundingRectangle.Top);
      Assert.True(settings.BoundingRectangle.Bottom <= row.BoundingRectangle.Bottom);
      session.MainWindow.SetForeground();
      session.WaitFor(() => settings.IsEnabled ? settings : null, "printer settings to become available");
      AutomationElement name = session.WaitFor(() => row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
         .Where(element => !element.IsOffscreen && element.BoundingRectangle.Width > 0 && element.BoundingRectangle.Height > 0
            && element.BoundingRectangle.Top >= row.BoundingRectangle.Top && element.BoundingRectangle.Bottom <= settings.BoundingRectangle.Top)
         .OrderBy(element => element.BoundingRectangle.Top).ThenBy(element => element.BoundingRectangle.Left).FirstOrDefault(), "visible compact printer header text above its inline actions");
      System.Drawing.Rectangle header = name.BoundingRectangle;
      Assert.True(header.Bottom <= settings.BoundingRectangle.Top, "The double-click target must be in the compact header, not selectable expanded details.");
      FlaUI.Core.Input.Mouse.DoubleClick(new System.Drawing.Point(header.Left + header.Width / 2, header.Top + header.Height / 2));
      AutomationElement nameBox = session.WaitForElement("MetadataName");
      Assert.True(nameBox.BoundingRectangle.Width > 600, "The responsive printer settings editor should use the wide dialog layout.");
      session.WaitForElement("MetadataLocation");
      session.WaitForElement("MetadataDescription");
      session.ClickButton("Cancel");
      session.WaitForElementGone("MetadataName");
      Assert.False(session.App.HasExited);
   }

   /// <summary>Uses existing job evidence only; the fixture must have at least one retained job.</summary>
   [Fact]
   public void ExistingJob_DoubleClickOpensStructuredDetailsWithoutMutation()
   {
      using var session = new AppSession();
      session.ClickElement("NavigateJobs");
      AutomationElement history = session.WaitForElement("RecentJobs");
      AutomationElement row = session.WaitFor(() => history.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)), "an existing retained job row");
      AutomationElement title = session.WaitFor(() => row.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text)), "job row text");
      session.MainWindow.SetForeground();
      title.DoubleClick();
      session.WaitForVisibleText("Outcome");
      session.WaitFor(() => session.FindInApp(cf => cf.ByControlType(ControlType.Text).And(cf.ByName("Event history"))), "structured event history");
      session.ClickButton("Close");
      AppSession.WaitUntilGone(() => session.FindInApp(cf => cf.ByControlType(ControlType.Text).And(cf.ByName("Event history"))), "job details to close");
      Assert.False(session.App.HasExited);
   }

   /// <summary>Header direction changes are presentation-only and do not change service job records.</summary>
   [Fact]
   public void SortingHeaders_ExposeDefaultsAndToggleDirection()
   {
      using var session = new AppSession();
      Assert.Equal("Sort by name, ascending", session.WaitForElement("SortPrinterName").Name);
      session.ClickElement("SortPrinterName");
      Assert.Equal("Sort by name, descending", session.WaitForElement("SortPrinterName").Name);
      session.ClickElement("SortPrinterLocation");
      Assert.Equal("Sort by location, ascending", session.WaitForElement("SortPrinterLocation").Name);
      session.ClickElement("NavigateJobs");
      Assert.Equal("Sort by created, descending", session.WaitForElement("SortJobCreated").Name);
      session.ClickElement("SortJobCreated");
      session.WaitFor(() => session.FindInApp(cf => cf.ByAutomationId("SortJobCreated")) is { Name: "Sort by created, ascending" } button ? button : null, "creation sort query to finish");
      session.ClickElement("SortJobTitle");
      session.WaitFor(() => session.FindInApp(cf => cf.ByAutomationId("SortJobTitle")) is { Name: "Sort by document, ascending" } button ? button : null, "document sort query to finish");
   }

   /// <summary>Requires an exposed PDF/ZPL queue; opens diagnostic choices and cancels without submitting any job.</summary>
   [Fact]
   public void LabelTestDialog_OffersResolutionAndStockChoicesWithoutPrinting()
   {
      using var session = new AppSession();
      AutomationElement inventory = session.WaitForElement("PrinterInventory");
      AutomationElement row = session.WaitFor(() => inventory.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
         .FirstOrDefault(IsSharedLabelRow), "an exposed PDF/ZPL queue for the read-only diagnostic dialog fixture");
      row.Patterns.SelectionItem.Pattern.Select();
      AutomationElement test = session.WaitFor(() => row.FindFirstDescendant(cf => cf.ByAutomationId("PrintTest")) is { IsEnabled: true } button ? button : null, "the selected queue's diagnostic button");
      test.Patterns.Invoke.Pattern.Invoke();
      ComboBox format = session.WaitForElement("TestPrintFormat").AsComboBox();
      ComboBox media = session.WaitForElement("TestPrintMedia").AsComboBox();
      Assert.Equal("Printer-ready label (ZPL)", format.SelectedItem?.Text);
      string[] resolutions = ["4 × 6 in label — 203 dpi", "4 × 6 in label — 300 dpi", "4 × 6 in label — 600 dpi"];
      foreach(string resolution in resolutions)
      {
         media.Select(resolution);
         Assert.Equal(resolution, media.SelectedItem?.Text);
      }
      format.Select("Document (PDF)");
      session.WaitFor(() => string.Equals(media.SelectedItem?.Text, "US Letter — 8.5 × 11 in", StringComparison.Ordinal) ? media : null, "PDF stock choices");
      string[] stocks = ["US Letter — 8.5 × 11 in", "A4 — 210 × 297 mm", "4 × 6 in label"];
      foreach(string stock in stocks)
      {
         media.Select(stock);
         Assert.Equal(stock, media.SelectedItem?.Text);
      }
      session.ClickButton("Back");
      session.WaitForElementGone("TestPrintFormat");
      Assert.False(session.App.HasExited);
   }

   private static bool IsSharedLabelRow(AutomationElement row)
   {
      AutomationElement? exposure = row.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox));
      if(row.IsOffscreen || exposure?.Patterns.Toggle.Pattern.ToggleState.Value != ToggleState.On)
      {
         return false;
      }
      return row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Any(element =>
      {
         string[] formats = element.Name.Split(',', StringSplitOptions.TrimEntries);
         return element.BoundingRectangle.Bottom <= row.BoundingRectangle.Top + 44
            && formats.Contains("ZPL", StringComparer.Ordinal) && formats.Contains("PDF", StringComparer.Ordinal);
      });
   }



   /// <summary>The close button hides details while the tray process stays alive.</summary>
   [Fact]
   public void Close_HidesDetailsAndKeepsTrayAlive()
   {
      using var session = new AppSession();
      session.WaitForElement("FooterStatus");
      nint originalWindow = session.MainWindow.Properties.NativeWindowHandle.Value;
      session.MainWindow.Close();
      session.WaitForWindowVisibility(originalWindow, visible: false);
      Assert.False(session.App.HasExited);
   }

   /// <summary>A second launch restores the hidden existing window and exits without another companion.</summary>
   [Fact]
   public async Task Relaunch_RestoresHiddenExistingCompanion()
   {
      using var session = new AppSession();
      session.WaitForElement("FooterStatus");
      int originalProcess = session.App.ProcessId;
      nint originalWindow = session.MainWindow.Properties.NativeWindowHandle.Value;
      session.MainWindow.Close();
      session.WaitForWindowVisibility(originalWindow, visible: false);
      using System.Diagnostics.Process second = session.Relaunch();
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
      await second.WaitForExitAsync(timeout.Token);
      Assert.Equal(0, second.ExitCode);
      session.WaitForWindowVisibility(originalWindow, visible: true);
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
      AutomationElement logWindow = session.WaitFor(
         () => session.TopLevelWindows().FirstOrDefault(window => string.Equals(window.Name, title, StringComparison.Ordinal)),
         "the activity log window");
      string[] controls = ["Follow", "Errors only", "Copy", "Open logs folder"];
      foreach(string control in controls)
      {
         session.WaitFor(() => logWindow.FindFirstDescendant(cf => cf.ByName(control)), "log toolbar control " + control);
      }
      session.WaitFor(() => logWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
         .FirstOrDefault(element => element.Name.Contains("[Companion]", StringComparison.Ordinal)), "companion events in the log viewer");
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

   /// <summary>Shared shell preferences persist without checking the network or changing machine startup.</summary>
   [Fact]
   public void AppKitPreferences_PersistAndExposeHelp()
   {
      using var session = new AppSession();
      session.ClickElement("NavigateSettings");
      Assert.StartsWith("Version ", session.WaitForElement("InstalledVersion").Name, StringComparison.Ordinal);
      Assert.StartsWith("Update channel: ", session.WaitForElement("UpdateChannel").Name, StringComparison.Ordinal);
      session.WaitForElement("CheckForUpdates");
      session.WaitForElement("ReleaseNotes");
      session.WaitForElement("UserGuide");
      session.WaitForElement("ReportIssue");
      session.WaitForElement("ProjectLink");
      AutomationElement updates = session.WaitForElement("CheckUpdatesAtStartup");
      Assert.Equal(ToggleState.Off, updates.Patterns.Toggle.Pattern.ToggleState.Value);
      AutomationElement duration = session.WaitForElement("SplashDuration");
      Assert.False(duration.IsEnabled);
      session.WaitForElement("SplashAtStartup").Patterns.Toggle.Pattern.Toggle();
      session.WaitFor(() => duration.IsEnabled ? duration : null, "splash duration to enable");
      duration.Patterns.RangeValue.Pattern.SetValue(1250);
      updates.Patterns.Toggle.Pattern.Toggle();
      using var saved = JsonDocument.Parse(session.ReadSettings());
      Assert.True(saved.RootElement.GetProperty("CheckForUpdatesOnStartup").GetBoolean());
      Assert.True(saved.RootElement.GetProperty("ShowSplashScreen").GetBoolean());
      Assert.Equal(1250, saved.RootElement.GetProperty("SplashMilliseconds").GetInt32());
      Assert.False(session.App.HasExited);
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
