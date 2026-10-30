/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using KofTwentyTwo.AppKit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Windows;


namespace Qqq.PrintAgent;

public sealed partial class MainWindow
{
   private bool _startupEnabled;
   private AgentServiceStatus? _serviceStatus;

   private async Task<bool> ConfirmAsync(string title, string message, string action)
   {
      if(_busy)
      {
         return false;
      }
      var dialog = new ContentDialog { Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = action, CloseButtonText = "Back", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
      return await DialogGuard.ShowAsync(dialog) == ContentDialogResult.Primary;
   }

   private bool IsActionError(string response)
   {
      if(!response.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
      {
         return false;
      }
      Feedback("Action could not be completed", response[6..], InfoBarSeverity.Error);
      return true;
   }

   private async Task RunActionAsync(PipeRequest request)
   {
      if(_busy)
      {
         return;
      }
      SetBusy(true);
      try
      {
         string response = await AgentPipeClient.SendAsync(request);
         if(!IsActionError(response))
         {
            string message = request.Operation switch
            {
               "set-device-profile" => "Computer details saved. They will synchronize when the service next contacts the server.",
               "set-printer-profile" => "Printer details saved. They will synchronize when the service next contacts the server.",
               "set-log-settings" => "Diagnostic log settings saved for the service and companion.",
               _ => response,
            };
            Feedback("Request completed", message, InfoBarSeverity.Success);
         }
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or OperationCanceledException)
      {
         Feedback("Result could not be confirmed", "Check Jobs and Logs before trying again. A timed-out request may already have reached the server or the printer.", InfoBarSeverity.Warning);
      }
      finally
      {
         SetBusy(false);
      }
      await RefreshAsync();
   }

   private async Task TestPrintAsync(PrinterSnapshot printer)
   {
      if(_busy)
      {
         return;
      }
      ComboBox format = new() { Header = "Test format", ItemsSource = printer.Formats, SelectedIndex = 0, MinWidth = 220 };
      StackPanel content = new() { Spacing = 12 };
      content.Children.Add(new TextBlock { Text = $"Send one test to {PrinterLabel(printer)} through the server? This uses real paper or a label. The printer must be shared here and approved on the server.", TextWrapping = TextWrapping.Wrap });
      content.Children.Add(format);
      var dialog = new ContentDialog { Title = "Print a test", Content = content, PrimaryButtonText = "Print one test", CloseButtonText = "Back", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
      if(await DialogGuard.ShowAsync(dialog) == ContentDialogResult.Primary && format.SelectedItem is string selectedFormat)
      {
         await RunActionAsync(new PipeRequest("test-print", PrinterId: printer.PrinterId, Format: selectedFormat));
      }
   }

   private async void PrinterSettings_Click(object sender, RoutedEventArgs e)
   {
      if(!await global::Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:printers")))
      {
         Feedback("Cannot open printer settings", "Open Windows Settings → Bluetooth & devices → Printers & scanners.", InfoBarSeverity.Warning);
      }
   }

   private void Services_Click(object sender, RoutedEventArgs e)
   {
      try
      {
         AgentServiceControl.OpenServices();
      }
      catch(Exception exception) when(exception is Win32Exception or InvalidOperationException)
      {
         Feedback("Cannot open Windows Services", exception.Message, InfoBarSeverity.Error);
      }
   }

   private async Task RefreshLifecycleAsync()
   {
      if(_loadingLifecycle)
      {
         return;
      }
      _loadingLifecycle = true;
      StartupToggle.IsEnabled = false;
      StartServiceButton.IsEnabled = false;
      RestartServiceButton.IsEnabled = false;
      try
      {
         (AgentServiceStatus Service, AgentStartupStatus Startup) state = await Task.Run(() => (Service: AgentServiceControl.GetStatus(), Startup: AgentStartup.GetStatus()));
         _serviceStatus = state.Service;
         ServiceStateText.Text = state.Service.Installed ? $"Windows service: {state.Service.State}" : "Windows service is not installed";
         ServiceGuidanceText.Text = state.Service.Guidance;
         StartServiceButton.IsEnabled = state.Service.CanStart;
         RestartServiceButton.IsEnabled = state.Service.CanRestart;
         _startupEnabled = state.Startup.Enabled;
         StartupToggle.IsOn = _startupEnabled;
         StartupToggle.IsEnabled = state.Startup.Available;
         StartupGuidanceText.Text = state.Startup.Guidance;
      }
      catch(Exception exception) when(exception is Win32Exception or InvalidOperationException or TimeoutException or UnauthorizedAccessException or IOException or System.Xml.XmlException or FormatException or OperationCanceledException)
      {
         ServiceStateText.Text = "Service status unavailable";
         ServiceGuidanceText.Text = "Open Windows Services to inspect the service, then refresh.";
         StartupGuidanceText.Text = "Startup status could not be read. The preference was not changed.";
         Feedback("Cannot read Windows settings", exception.Message, InfoBarSeverity.Error);
      }
      finally
      {
         _loadingLifecycle = false;
      }
   }

   private async void StartService_Click(object sender, RoutedEventArgs e) => await ChangeServiceAsync(restart: false);

   private async void RestartService_Click(object sender, RoutedEventArgs e)
   {
      if(await ConfirmAsync("Restart the print service?", "A job being submitted could have an uncertain outcome. Wait for active jobs to finish when possible.", "Restart service"))
      {
         await ChangeServiceAsync(restart: true);
      }
   }

   private async Task ChangeServiceAsync(bool restart)
   {
      if(_busy)
      {
         return;
      }
      SetBusy(true);
      StartServiceButton.IsEnabled = false;
      RestartServiceButton.IsEnabled = false;
      try
      {
         AgentServiceStatus status = restart ? await AgentServiceControl.RestartAsync() : await AgentServiceControl.StartAsync();
         Feedback("Windows service", status.Guidance, InfoBarSeverity.Success);
      }
      catch(Exception exception) when(exception is Win32Exception or InvalidOperationException or TimeoutException or UnauthorizedAccessException or IOException or System.Xml.XmlException or OperationCanceledException)
      {
         Feedback("Service action failed", exception.Message, InfoBarSeverity.Error);
      }
      finally
      {
         SetBusy(false);
      }
      await RefreshLifecycleAsync();
      await RefreshAsync();
   }

   private async void Startup_Toggled(object sender, RoutedEventArgs e)
   {
      if(_initializing || _loadingLifecycle || StartupToggle.IsOn == _startupEnabled)
      {
         return;
      }
      _loadingLifecycle = true;
      StartupToggle.IsEnabled = false;
      try
      {
         AgentStartupStatus status = await AgentStartup.SetEnabledAsync(StartupToggle.IsOn);
         _startupEnabled = status.Enabled;
         StartupGuidanceText.Text = status.Guidance;
      }
      catch(Exception exception) when(exception is Win32Exception or InvalidOperationException or TimeoutException or UnauthorizedAccessException or IOException or System.Xml.XmlException or OperationCanceledException)
      {
         Feedback("Startup preference not saved", exception.Message, InfoBarSeverity.Error);
      }
      finally
      {
         StartupToggle.IsOn = _startupEnabled;
         _loadingLifecycle = false;
      }
      await RefreshLifecycleAsync();
   }
}
