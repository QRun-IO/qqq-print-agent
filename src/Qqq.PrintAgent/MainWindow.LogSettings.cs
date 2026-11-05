/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using KofTwentyTwo.AppKit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

/// <summary>
/// Log settings part of the main window: retention, file size rollover, kept segments and debug detail for the shared diagnostic log, and
/// clearing it. The values are applied by the service, which owns the log files, so the service and companion always share one policy.
/// </summary>
public sealed partial class MainWindow
{
   /// <summary>
   /// Shows the current retention, file size limit, kept segments and debug switch (with the current file's size and how many times it rolled
   /// over today) in a dialog and, if the operator saves valid values, sends them to the service. Files rotate at the start of the next local
   /// day and whenever one reaches the size limit.
   /// </summary>
   private async void LogSettings_Click(object sender, RoutedEventArgs e)
   {
      LogSettings? settings = await ReadLogSettingsAsync();
      if(settings is null || _updatesClosed)
      {
         return;
      }
      NumberBox retention = new() { Header = "Retention (days)", Minimum = 1, Maximum = 365, Value = settings.RetentionDays, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
      NumberBox limit = new() { Header = "File size limit before rollover (MiB)", Minimum = 1, Maximum = 1024, Value = settings.MaxFileMegabytes, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
      NumberBox segments = new() { Header = "Rolled-over segments kept per day", Minimum = 1, Maximum = 100, Value = settings.SegmentsPerDay, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
      ToggleSwitch debug = new() { Header = "Debug logging", OnContent = "On: every request, response and frame is logged", OffContent = "Off: steps, warnings and errors only", IsOn = settings.DebugLogging };
      StackPanel content = new() { Spacing = 12 };
      content.Children.Add(new TextBlock { Text = "Logs start a new file each local day and roll over to a numbered segment when a file reaches the size limit; logging never stops because of size. These settings apply to both service and companion logs.", TextWrapping = TextWrapping.Wrap });
      content.Children.Add(retention);
      content.Children.Add(limit);
      content.Children.Add(segments);
      content.Children.Add(debug);
      content.Children.Add(new TextBlock { Text = "Debug logging adds request routes, status codes, timings and realtime frames. Credentials, documents and enrollment codes are never logged at any level.", TextWrapping = TextWrapping.Wrap, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
      string usage = $"Current log file: {settings.CurrentFileBytes / (1024d * 1024d):0.0} MiB; rolled over {settings.SegmentsToday} time(s) today.";
      content.Children.Add(new TextBlock { Text = usage, TextWrapping = TextWrapping.Wrap });
      var dialog = new ContentDialog { Title = "Log settings", Content = content, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot };
      if(await DialogGuard.ShowAsync(dialog) == ContentDialogResult.Primary)
      {
         if(double.IsNaN(retention.Value) || double.IsNaN(limit.Value) || double.IsNaN(segments.Value))
         {
            Feedback("Log settings not saved", "Enter valid retention, file size and segment values.", InfoBarSeverity.Warning);
            return;
         }
         App.Log.Info($"[Companion] Log settings saved: retention {(int)retention.Value} days, file size {(int)limit.Value} MiB, {(int)segments.Value} segments per day, debug logging {(debug.IsOn ? "on" : "off")}.");
         await RunActionAsync(new PipeRequest("set-log-settings", LogSettings: new LogSettings((int)retention.Value, (int)limit.Value, (int)segments.Value, debug.IsOn)));
      }
   }



   /// <summary>Asks the service for the current log settings while the window is busy; a failure shows a message and returns null so the dialog never opens with made-up numbers.</summary>
   private async Task<LogSettings?> ReadLogSettingsAsync()
   {
      if(_busy)
      {
         return null;
      }
      SetBusy(true);
      try
      {
         string response = await _sendRequest(new PipeRequest("get-log-settings"));
         return IsActionError(response) ? null : JsonSerializer.Deserialize<LogSettings>(response, PrintApiClient.JsonOptions);
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException)
      {
         Feedback("Log settings unavailable", "Check the local service and try again.", InfoBarSeverity.Error);
         return null;
      }
      finally
      {
         SetBusy(false);
      }
   }



   /// <summary>Clears the diagnostic logs only after the operator confirms; the prompt states that job history, printer settings and print jobs are kept.</summary>
   private async void ClearLogs_Click(object sender, RoutedEventArgs e) => await ClearLogsAsync(Content.XamlRoot);



   /// <summary>Confirms in the window the operator is looking at, then asks the service (which owns the files) to clear the logs.</summary>
   private async Task ClearLogsAsync(XamlRoot owner)
   {
      if(await ConfirmAsync("Clear local diagnostic logs?", "This permanently deletes retained service and companion diagnostic logs. Print jobs, printer settings and job history are preserved. New events will start a fresh log.", "Clear logs", owner))
      {
         await RunActionAsync(new PipeRequest("clear-logs"));
      }
   }



   /// <summary>
   /// Opens (or activates) the shared AppKit log viewer and adds this application's "Clear logs" button to its toolbar. The viewer
   /// is sealed and has no command hook, so the button is placed by walking its toolbar once per window; if the viewer's layout
   /// ever changes the viewer still opens and the Settings page keeps its own Clear local logs button.
   /// </summary>
   private void ShowActivityLog()
   {
      var viewer = LogWindow.ShowSingle(App.Log, AppIdentity.Info);
      if(viewer.Content is not Grid { Children: [StackPanel toolbar, ..] } || toolbar.Children.OfType<Button>().Any(button => string.Equals(AutomationProperties.GetAutomationId(button), "ClearLogsFromViewer", StringComparison.Ordinal)))
      {
         return;
      }
      var clear = new Button { Content = "Clear logs" };
      AutomationProperties.SetAutomationId(clear, "ClearLogsFromViewer");
      clear.Click += async (_, _) => await ClearLogsAsync(viewer.Content.XamlRoot);
      int position = toolbar.Children.TakeWhile(child => child is ButtonBase).Count();
      toolbar.Children.Insert(position, clear);
   }
}
