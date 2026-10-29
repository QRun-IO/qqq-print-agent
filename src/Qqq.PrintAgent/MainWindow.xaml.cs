/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using KofTwentyTwo.AppKit.Settings;
using KofTwentyTwo.AppKit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Qqq.PrintAgent.Windows;


namespace Qqq.PrintAgent;

/// <summary>AppKit companion for enrollment and diagnostics of the machine print service.</summary>
public sealed partial class MainWindow : Window, IDisposable
{
   private readonly AppSettings _settings = AppIdentity.SettingsStore.Load();
   private bool _busy;
   private bool _exitRequested;
   private readonly TaskbarIcon _tray;
   private readonly MenuFlyoutItem _trayStatus;
   private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
   private string? _currentHealth;



   /// <summary>Initialize the shared shell and the printing setup screen.</summary>
   public MainWindow()
   {
      InitializeComponent();
      Title = AppIdentity.Info.DisplayName;
      this.ResizeForDpi(960, 820);
      this.SetMinimumSize(680, 600);
      this.SetAppIcon(AppIdentity.Info);
      SplashToggle.IsChecked = _settings.ShowSplashScreen;
      (_settings.ThemeKind switch { AppTheme.Light => ThemeLight, AppTheme.Dark => ThemeDark, _ => ThemeSystem }).IsChecked = true;
      SplashOverlay.ShowOver(RootGrid, AppIdentity.Info, _settings);
      _trayStatus = new MenuFlyoutItem { Text = "Red — service not connected", Command = new RelayCommand(ShowDetails) };
      MenuFlyout trayMenu = new();
      trayMenu.Items.Add(_trayStatus);
      trayMenu.Items.Add(new MenuFlyoutItem { Text = "Open details", Command = new RelayCommand(ShowDetails) });
      trayMenu.Items.Add(new MenuFlyoutItem { Text = "Refresh status", Command = new AsyncRelayCommand(RefreshAsync) });
      trayMenu.Items.Add(new MenuFlyoutSeparator());
      trayMenu.Items.Add(new MenuFlyoutItem { Text = "Exit companion (printing continues)", Command = new RelayCommand(ExitCompanion) });
      _tray = new TaskbarIcon { ToolTipText = "QQQ Print Agent — service not connected", ContextFlyout = trayMenu, LeftClickCommand = new RelayCommand(ShowDetails) };
      SetHealth("RED");
      _tray.ForceCreate(enablesEfficiencyMode: false);
      _refreshTimer.Tick += async (_, _) => await RefreshAsync();
      _refreshTimer.Start();
      AppWindow.Closing += (_, args) =>
      {
         if(!_exitRequested)
         {
            args.Cancel = true;
            AppWindow.Hide();
         }
      };
      Closed += (_, _) => Dispose();
   }



   /// <summary>Release the tray icon and polling timer when the companion exits.</summary>
   public void Dispose()
   {
      _refreshTimer.Stop();
      _tray.Dispose();
      LogWindow.CloseCurrent();
      GC.SuppressFinalize(this);
   }



   /// <summary>Read service status once the window has loaded.</summary>
   private async void Root_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();



   /// <summary>Enroll through the service; the UI never receives or stores the device token.</summary>
   private async void Enroll_Click(object sender, RoutedEventArgs e)
   {
      if(_busy)
      {
         return;
      }
      string address = ServerBox.Text.Trim();
      if(!Uri.TryCreate(address, UriKind.Absolute, out Uri? server)
         || (!string.Equals(server.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) && !(server.IsLoopback && string.Equals(server.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)))
         || !string.IsNullOrEmpty(server.UserInfo) || !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.Fragment)
         || string.IsNullOrWhiteSpace(EnrollmentBox.Password))
      {
         Feedback("Check the connection details", "Enter an HTTPS server address and the enrollment code from QQQ. Localhost HTTP is supported for development.", InfoBarSeverity.Warning);
         return;
      }
      SetBusy(true);
      try
      {
         Feedback("Connecting", "The service is enrolling this device with QQQ…", InfoBarSeverity.Informational);
         string result = await AgentPipeClient.SendAsync(new PipeRequest("enroll", address.TrimEnd('/'), EnrollmentBox.Password.Trim()));
         if(!result.StartsWith("Enrolled device ", StringComparison.Ordinal))
         {
            Feedback("Enrollment failed", result, InfoBarSeverity.Error);
            return;
         }
         Feedback("Device connected", result + ". Select which printers to expose here, then approve the discovered queues in QQQ.", InfoBarSeverity.Success);
         StatusText.Text = result;
         App.Log.Info("Print device enrollment completed.");
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or OperationCanceledException)
      {
         ServiceUnavailable();
      }
      finally
      {
         EnrollmentBox.Password = string.Empty;
         SetBusy(false);
      }
   }



   /// <summary>Refresh the local service and the queues visible under its identity.</summary>
   private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();



   /// <summary>Query diagnostic information without exposing credentials.</summary>
   private async Task RefreshAsync()
   {
      if(_busy)
      {
         return;
      }
      SetBusy(true);
      try
      {
         string response = await AgentPipeClient.SendAsync(new PipeRequest("snapshot"));
         AgentSnapshot snapshot = JsonSerializer.Deserialize<AgentSnapshot>(response, Qqq.PrintAgent.Core.Printing.PrintApiClient.JsonOptions)
            ?? throw new JsonException("Missing snapshot.");
         RenderSnapshot(snapshot);
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or OperationCanceledException)
      {
         ServiceUnavailable();
      }
      catch(Exception exception) when(exception is JsonException or InvalidOperationException or KeyNotFoundException)
      {
         SetHealth("RED");
         Feedback("Cannot read service response", "Install matching versions of the QQQ Print Agent companion and service, then refresh.", InfoBarSeverity.Error);
      }
      finally
      {
         SetBusy(false);
      }
   }



   /// <summary>Give actionable guidance without logging pipe payloads or secrets.</summary>
   private void ServiceUnavailable()
   {
      SetHealth("RED");
      StatusText.Text = "Local print service unavailable";
      Feedback("Service unavailable", "Install and start the QQQ Print Agent Windows service, then refresh. This administration window needs administrator access.", InfoBarSeverity.Error);
   }



   /// <summary>Prevent overlapping local administration requests.</summary>
   private void SetBusy(bool busy)
   {
      _busy = busy;
      EnrollButton.IsEnabled = !busy;
      RefreshButton.IsEnabled = !busy;
      foreach(StackPanel row in PrintersList.Items.OfType<StackPanel>())
      {
         foreach(CheckBox checkbox in row.Children.OfType<CheckBox>())
         {
            checkbox.IsEnabled = !busy;
         }
      }
   }



   /// <summary>Present the latest operation's result.</summary>
   private void Feedback(string title, string message, InfoBarSeverity severity)
   {
      FeedbackBar.Title = title;
      FeedbackBar.Message = message;
      FeedbackBar.Severity = severity;
      FeedbackBar.IsOpen = true;
   }



   /// <summary>Hide details while the tray continues monitoring.</summary>
   private void Hide_Click(object sender, RoutedEventArgs e) => AppWindow.Hide();



   /// <summary>Close only the companion; the service remains running.</summary>
   private void Exit_Click(object sender, RoutedEventArgs e) => ExitCompanion();



   /// <summary>Remove the tray icon and close the companion deliberately.</summary>
   private void ExitCompanion()
   {
      _exitRequested = true;
      Close();
   }



   /// <summary>Restore details from the tray without restarting the service.</summary>
   private void ShowDetails()
   {
      AppWindow.Show();
      Activate();
   }



   /// <summary>Render the service's authoritative health, queues, jobs and bounded logs.</summary>
   private void RenderSnapshot(AgentSnapshot snapshot)
   {
      SetHealth(snapshot.Health);
      StatusText.Text = snapshot.Status;
      ConnectionText.Text = snapshot.Enrolled
         ? $"Server: {snapshot.ServerUrl}\nDevice: {snapshot.DeviceId}\nLast successful poll: {snapshot.LastSuccessfulPoll ?? "Not yet"}"
         : "This device has not been enrolled. Open Connection settings to connect it to QQQ.";
      if(string.IsNullOrWhiteSpace(ServerBox.Text) && snapshot.ServerUrl is not null)
      {
         ServerBox.Text = snapshot.ServerUrl;
      }
      PrintersList.Items.Clear();
      foreach(PrinterSnapshot printer in snapshot.Printers)
      {
         CheckBox exposure = new() { Content = $"Expose {printer.Name} to QQQ", IsChecked = printer.Exposed, Tag = printer, IsEnabled = !_busy };
         exposure.Click += Exposure_Click;
         StackPanel row = new() { Spacing = 4, Margin = new Thickness(0, 6, 0, 6) };
         row.Children.Add(exposure);
         row.Children.Add(new TextBlock { Text = $"{printer.Status} · {string.Join(", ", printer.Formats)} · {(printer.Exposed ? "Exposed" : "Local only")}\nDriver: {printer.DriverName} · Port: {printer.PortName}\nPrinter ID: {printer.PrinterId}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
         PrintersList.Items.Add(row);
      }
      PrintersSummary.Text = $"{snapshot.Printers.Count} printer(s) visible · {snapshot.Printers.Count(printer => printer.Exposed)} exposed to QQQ";
      JobsList.ItemsSource = snapshot.Jobs.Select(job => $"{job.Status} · {job.UpdatedAt}\nJob: {job.JobId} · Spooler: {job.SpoolerJobId ?? "Not assigned"}\n{job.Message}").ToArray();
      JobsSummary.Text = snapshot.Jobs.Count == 0 ? "No local print jobs yet." : $"{snapshot.Jobs.Count} recent job(s)";
      ServiceLogsList.ItemsSource = snapshot.Logs.Select(entry => $"{entry.Timestamp} [{entry.Level}] {entry.Message}").ToArray();
      Feedback(HealthText.Text, snapshot.Status, snapshot.Health switch { "GREEN" => InfoBarSeverity.Success, "ORANGE" => InfoBarSeverity.Warning, _ => InfoBarSeverity.Error });
   }



   /// <summary>Persist local printer exposure through the protected service pipe.</summary>
   private async void Exposure_Click(object sender, RoutedEventArgs e)
   {
      if(sender is not CheckBox { Tag: PrinterSnapshot printer } checkbox || _busy)
      {
         return;
      }
      SetBusy(true);
      try
      {
         string response = await AgentPipeClient.SendAsync(new PipeRequest("set-exposure", PrinterId: printer.PrinterId, Exposed: checkbox.IsChecked == true));
         AgentSnapshot snapshot = JsonSerializer.Deserialize<AgentSnapshot>(response, Qqq.PrintAgent.Core.Printing.PrintApiClient.JsonOptions)
            ?? throw new JsonException("Missing snapshot.");
         RenderSnapshot(snapshot);
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException)
      {
         checkbox.IsChecked = printer.Exposed;
         Feedback("Printer setting not saved", "The service did not confirm this change. Refresh and try again.", InfoBarSeverity.Error);
      }
      finally
      {
         SetBusy(false);
      }
   }



   /// <summary>Pair tray color with a text status so color is never the only signal.</summary>
   private void SetHealth(string health)
   {
      string label = health switch { "GREEN" => "Green — connected and ready", "ORANGE" => "Orange — connected, printers need attention", _ => "Red — disconnected or failed" };
      HealthText.Text = label;
      _trayStatus.Text = label;
      _tray.ToolTipText = "QQQ Print Agent: " + label;
      if(string.Equals(_currentHealth, health, StringComparison.Ordinal))
      {
         return;
      }
      _currentHealth = health;
      global::Windows.UI.Color color = health switch { "GREEN" => Microsoft.UI.Colors.LimeGreen, "ORANGE" => Microsoft.UI.Colors.Orange, _ => Microsoft.UI.Colors.Red };
      SolidColorBrush brush = new(color);
      HealthDot.Fill = brush;
      _tray.IconSource = new GeneratedIconSource { Text = "●", FontSize = 64, Foreground = brush };
   }



   /// <summary>Apply and save the AppKit theme.</summary>
   private void Theme_Click(object sender, RoutedEventArgs e)
   {
      _settings.Theme = (string)((FrameworkElement)sender).Tag;
      WinUIShell.ApplyTheme(RootGrid, _settings.ThemeKind);
      Save();
   }



   /// <summary>Save the splash preference.</summary>
   private void Splash_Click(object sender, RoutedEventArgs e)
   {
      _settings.ShowSplashScreen = SplashToggle.IsChecked;
      Save();
   }



   /// <summary>Show the shared AppKit activity log.</summary>
   private void ActivityLog_Click(object sender, RoutedEventArgs e) => LogWindow.ShowSingle(App.Log, AppIdentity.Info);



   /// <summary>Show the shared AppKit About dialog.</summary>
   private async void About_Click(object sender, RoutedEventArgs e) => await AboutDialog.ShowAsync(AppIdentity.Info, Content.XamlRoot);



   /// <summary>Persist presentation preferences only.</summary>
   private void Save()
   {
      if(!AppIdentity.SettingsStore.Save(_settings))
      {
         Feedback("Settings not saved", "The window preference could not be saved.", InfoBarSeverity.Warning);
      }
   }
}

