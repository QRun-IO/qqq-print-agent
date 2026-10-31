using System.Text.Json;
using KofTwentyTwo.AppKit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

public sealed partial class MainWindow
{
   private async void LogSettings_Click(object sender, RoutedEventArgs e)
   {
      LogSettings? settings = await ReadLogSettingsAsync();
      if(settings is null || _updatesClosed)
      {
         return;
      }
      NumberBox retention = new() { Header = "Retention (days)", Minimum = 1, Maximum = 365, Value = settings.RetentionDays, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
      NumberBox limit = new() { Header = "Daily size limit (MiB)", Minimum = 1, Maximum = 100, Value = settings.MaxDailyMegabytes, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
      StackPanel content = new() { Spacing = 12 };
      content.Children.Add(new TextBlock { Text = "Rotation: daily, at the start of the next local calendar day. These limits apply to both service and companion logs.", TextWrapping = TextWrapping.Wrap });
      content.Children.Add(retention);
      content.Children.Add(limit);
      string usage = $"Current daily log: {settings.CurrentDayBytes / (1024d * 1024d):0.0} MiB.";
      content.Children.Add(new TextBlock { Text = usage + (settings.DailyLimitReached ? " The daily limit has been reached. Increase the limit, clear local logs, or wait until the next local day to resume diagnostic logging." : ""), TextWrapping = TextWrapping.Wrap });
      var dialog = new ContentDialog { Title = "Log settings", Content = content, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot };
      if(await DialogGuard.ShowAsync(dialog) == ContentDialogResult.Primary)
      {
         if(double.IsNaN(retention.Value) || double.IsNaN(limit.Value))
         {
            Feedback("Log settings not saved", "Enter valid retention and daily size values.", InfoBarSeverity.Warning);
            return;
         }
         await RunActionAsync(new PipeRequest("set-log-settings", LogSettings: new LogSettings((int)retention.Value, (int)limit.Value)));
      }
   }

   private async Task<LogSettings?> ReadLogSettingsAsync()
   {
      if(_busy)
      {
         return null;
      }
      SetBusy(true);
      try
      {
         string response = await AgentPipeClient.SendAsync(new PipeRequest("get-log-settings"));
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

   private async void ClearLogs_Click(object sender, RoutedEventArgs e)
   {
      if(await ConfirmAsync("Clear local diagnostic logs?", "This permanently deletes retained service and companion diagnostic logs. Print jobs, printer settings and job history are preserved. New events will start a fresh log.", "Clear logs"))
      {
         await RunActionAsync(new PipeRequest("clear-logs"));
      }
   }
}
