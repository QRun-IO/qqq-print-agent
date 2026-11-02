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

/// <summary>
/// Operator actions part of the main window: confirmations, requests that change service state (test print, retry, cancel,
/// review, settings), and the Windows service and sign-in startup controls. Each action runs only when no other one is
/// running and always refreshes afterwards, so the screen shows what the service really did.
/// </summary>
public sealed partial class MainWindow
{
   private bool _startupEnabled;
   private AgentServiceStatus? _serviceStatus;



   /// <summary>
   /// Asks the operator to confirm an action in a dialog whose default button is Back, so pressing Enter cancels rather than
   /// proceeds. It declines without showing anything while another request is running or the window is closing.
   /// </summary>
   private async Task<bool> ConfirmAsync(string title, string message, string action)
   {
      if(_updatesClosed || _busy)
      {
         return false;
      }
      var dialog = new ContentDialog { Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = action, CloseButtonText = "Back", DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot };
      return await DialogGuard.ShowAsync(dialog) == ContentDialogResult.Primary;
   }



   /// <summary>Recognises the service's "Error:" reply, shows its text as feedback and reports true, so callers can stop instead of parsing an error as data.</summary>
   private bool IsActionError(string response)
   {
      if(!response.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
      {
         return false;
      }
      Feedback("Action could not be completed", response[6..], InfoBarSeverity.Error);
      return true;
   }



   /// <summary>
   /// Sends one state-changing request to the service while the window is marked busy. A timeout or broken pipe is reported
   /// as "result could not be confirmed" with advice to check Jobs and Logs, because the request may already have reached the
   /// server or the printer and must not be blindly repeated. The list is refreshed afterwards either way.
   /// </summary>
   private async Task RunActionAsync(PipeRequest request)
   {
      if(_updatesClosed || _busy)
      {
         return;
      }
      SetBusy(true);
      try
      {
         string response = await _sendRequest(request);
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



   /// <summary>Lets the operator choose one built-in diagnostic (format and profile) for a printer and sends exactly one request when they confirm; Back is the default so nothing prints by accident.</summary>
   private async Task TestPrintAsync(PrinterSnapshot printer)
   {
      if(_updatesClosed || _busy)
      {
         return;
      }
      var editor = new TestPrintEditor(printer, PrinterLabel(printer));
      ContentDialog dialog = CreateWideDialog("Print a test", editor);
      dialog.PrimaryButtonText = "Print one test";
      dialog.CloseButtonText = "Back";
      dialog.DefaultButton = ContentDialogButton.Close;
      if(await DialogGuard.ShowAsync(dialog) == ContentDialogResult.Primary && editor.SelectedProfile is string profile)
      {
         await RunActionAsync(new PipeRequest("test-print", PrinterId: printer.PrinterId, Format: editor.SelectedFormat, TestProfile: profile));
      }
   }



   /// <summary>Opens the Windows printer settings page, with written directions as a fallback when Windows will not launch it.</summary>
   private async void PrinterSettings_Click(object sender, RoutedEventArgs e)
   {
      if(!await global::Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:printers")))
      {
         Feedback("Cannot open printer settings", "Open Windows Settings → Bluetooth & devices → Printers & scanners.", InfoBarSeverity.Warning);
      }
   }



   /// <summary>Opens the Windows Services console so the operator can inspect the print service, and reports a failure to start it as feedback.</summary>
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



   /// <summary>
   /// Reads the Windows service and sign-in task state off the UI thread and updates the Settings controls. Controls stay
   /// disabled while the read runs and when it fails, so no action is offered on an unknown state, and overlapping reads are ignored.
   /// </summary>
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
         if(_updatesClosed)
         {
            return;
         }
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



   /// <summary>Starts the Windows service from its button; the work is shared with restart in <c>ChangeServiceAsync</c>.</summary>
   private async void StartService_Click(object sender, RoutedEventArgs e) => await ChangeServiceAsync(restart: false);



   /// <summary>Restarts the service only after the operator confirms, because a job being submitted could be left with an uncertain outcome.</summary>
   private async void RestartService_Click(object sender, RoutedEventArgs e)
   {
      if(await ConfirmAsync("Restart the print service?", "A job being submitted could have an uncertain outcome. Wait for active jobs to finish when possible.", "Restart service"))
      {
         await ChangeServiceAsync(restart: true);
      }
   }



   /// <summary>Starts or restarts the Windows service with the window marked busy and both service buttons disabled, then re-reads the service state and the snapshot so the screen reflects the result.</summary>
   private async Task ChangeServiceAsync(bool restart)
   {
      if(_updatesClosed || _busy)
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



   /// <summary>
   /// Turns the sign-in startup task on or off. It ignores changes made by the window itself while loading, applies the
   /// choice through the startup helper, and always resets the switch to the real saved state afterwards, so the display never
   /// shows a preference that was not actually applied.
   /// </summary>
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
