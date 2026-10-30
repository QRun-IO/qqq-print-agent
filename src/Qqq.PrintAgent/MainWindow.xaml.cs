/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using KofTwentyTwo.AppKit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Qqq.PrintAgent.Windows;


namespace Qqq.PrintAgent;

/// <summary>AppKit companion for enrollment and diagnostics of the machine print service.</summary>
public sealed partial class MainWindow : Window, IDisposable
{
   private readonly AppSettings _settings = AppIdentity.SettingsStore.Load();
   private bool _busy;
   private bool _refreshing;
   private int _mutationVersion;
   private bool _exitRequested;
   private readonly bool _initializing = true;
   private readonly TaskbarIcon _tray;
   private readonly MenuFlyoutItem _trayStatus;
   private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
   private string? _currentHealth;
   private readonly Dictionary<string, PrinterRowView> _printerRows = new(StringComparer.Ordinal);
   private bool _loadingLifecycle;
   private readonly Dictionary<string, System.Drawing.Icon> _trayIcons = new(StringComparer.Ordinal);
   private readonly RegisteredWaitHandle? _activationRegistration;



   /// <summary>Initialize the shared shell and the printing setup screen.</summary>
   public MainWindow()
   {
      InitializeComponent();
      ApplyBranding();
      this.ResizeForDpi(1100, 760);
      this.SetMinimumSize(800, 560);
      this.SetAppIcon(AppIdentity.Info);
      InitializeAppKitSettings();
      Navigation.SelectedItem = PrintersNavigation;
      Navigation.Loaded += Navigation_Loaded;
      _initializing = false;
      SplashOverlay.ShowOver(RootGrid, AppIdentity.Info, _settings);
      _trayStatus = new MenuFlyoutItem { Text = "Red — service not connected", Command = new RelayCommand(ShowDetails) };
      MenuFlyout trayMenu = new();
      trayMenu.Items.Add(_trayStatus);
      trayMenu.Items.Add(new MenuFlyoutItem { Text = "Open details", Command = new RelayCommand(ShowDetails) });
      trayMenu.Items.Add(new MenuFlyoutItem { Text = "Refresh status", Command = new AsyncRelayCommand(RefreshAsync) });
      trayMenu.Items.Add(new MenuFlyoutSeparator());
      trayMenu.Items.Add(new MenuFlyoutItem { Text = "Exit companion (printing continues)", Command = new RelayCommand(ExitCompanion) });
      _tray = new TaskbarIcon { ToolTipText = TrayTooltip("Service not connected"), ContextFlyout = trayMenu, LeftClickCommand = new RelayCommand(ShowDetails) };
      SetHealth("RED");
      _tray.ForceCreate(enablesEfficiencyMode: false);
      App.Log.Info($"Tray icon created: {_tray.IsCreated}.");
      _refreshTimer.Tick += async (_, _) => await RefreshAsync();
      _refreshTimer.Start();
      AppWindow.Closing += (_, args) =>
      {
         if(!_exitRequested && _tray.IsCreated)
         {
            args.Cancel = true;
            AppWindow.Hide();
         }
      };
      Closed += (_, _) => Dispose();
      if(Program.ActivationSignal is EventWaitHandle signal)
      {
         _activationRegistration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => DispatcherQueue.TryEnqueue(ShowDetails), state: null, Timeout.Infinite, executeOnlyOnce: false);
      }
   }



   private void ApplyBranding()
   {
      Title = AppIdentity.Info.DisplayName;
      ProductHeading.Text = Title;
      ToolTipService.SetToolTip(ProductHeading, Title);
      AboutButton.Content = "About " + Title;
      CompanyHeading.Text = AppIdentity.Branding.CompanyName ?? string.Empty;
      CompanyHeading.Visibility = AppIdentity.Branding.CompanyName is null ? Visibility.Collapsed : Visibility.Visible;
      SupportLink.NavigateUri = AppIdentity.Branding.SupportUrl ?? new Uri("https://github.com/QRun-IO/qqq-print-agent/issues");
      SupportLink.Visibility = Visibility.Visible;
      if(AppIdentity.Branding.LogoPath is string logo)
      {
         CustomerLogo.ImageFailed += (_, _) => CustomerLogo.Visibility = Visibility.Collapsed;
         CustomerLogo.Source = new BitmapImage(new Uri(logo));
         CustomerLogo.Visibility = Visibility.Visible;
      }
   }


   /// <summary>Release the tray icon and polling timer when the companion exits.</summary>
   public void Dispose()
   {
      DisposeUpdates();
      _refreshTimer.Stop();
      _activationRegistration?.Unregister(null);
      _tray.Dispose();
      foreach(System.Drawing.Icon icon in _trayIcons.Values)
      {
         icon.Dispose();
      }
      LogWindow.CloseCurrent();
      GC.SuppressFinalize(this);
   }



   /// <summary>Read service status once the window has loaded.</summary>
   private async void Root_Loaded(object sender, RoutedEventArgs e)
   {
      await Task.WhenAll(RefreshAsync(), RefreshLifecycleAsync(), CheckUpdatesAfterStartupAsync());
   }

   private void Navigation_Loaded(object sender, RoutedEventArgs e)
   {
      // The built-in settings item is created by the template, after construction.
      if(Navigation.SettingsItem is DependencyObject settingsItem)
      {
         Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(settingsItem, "NavigateSettings");
         if(settingsItem is Control control)
         {
            control.AccessKey = "S";
            ToolTipService.SetToolTip(control, "Settings (Ctrl+Shift+S)");
         }
      }
   }

   /// <summary>Apply tray-only startup after App has completed the initial Window.Activate call.</summary>
   internal void ApplyStartupVisibility()
   {
      if(Environment.GetCommandLineArgs().Contains("--tray", StringComparer.Ordinal) && _tray.IsCreated)
      {
         AppWindow.Hide();
      }
   }

   private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
   {
      if(PageTitle is null)
      {
         return;
      }
      string page = args.IsSettingsSelected ? "Settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "Printers";
      PrintersPage.Visibility = string.Equals(page, "Printers", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
      JobsPage.Visibility = string.Equals(page, "Jobs", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
      LogsPage.Visibility = string.Equals(page, "Logs", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
      SettingsPage.Visibility = string.Equals(page, "Settings", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
      PageTitle.Text = page;
      PageDescription.Text = page switch
      {
         "Jobs" => "Recent jobs received by this computer.",
         "Logs" => "Activity and diagnostics in the AppKit log viewer.",
         "Settings" => "Connection, appearance and application information.",
         _ => "Choose which printers this computer shares with the server.",
      };
      if(string.Equals(page, "Logs", StringComparison.Ordinal))
      {
         LogWindow.ShowSingle(App.Log, AppIdentity.Info);
      }
      if(args.IsSettingsSelected && !_initializing)
      {
         await RefreshLifecycleAsync();
      }
      if(string.Equals(page, "Jobs", StringComparison.Ordinal) && !_initializing)
      {
         await RefreshJobPageAsync();
      }
   }

   private async void Connection_Click(object sender, RoutedEventArgs e) => await ShowConnectionAsync();

   private async Task ShowConnectionAsync()
   {
      ConnectionDialog.XamlRoot = Content.XamlRoot;
      await DialogGuard.ShowAsync(ConnectionDialog);
   }



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
         Feedback("Check the connection details", "Enter an HTTPS server address and the enrollment code from the server. Localhost HTTP is supported for development.", InfoBarSeverity.Warning);
         return;
      }
      SetBusy(true);
      try
      {
         Feedback("Connecting", "The service is enrolling this device with the server…", InfoBarSeverity.Informational);
         string result = await AgentPipeClient.SendAsync(new PipeRequest("enroll", address.TrimEnd('/'), EnrollmentBox.Password.Trim()));
         if(!result.StartsWith("Enrolled device ", StringComparison.Ordinal))
         {
            Feedback("Enrollment failed", result + " If the code expired or was already used, generate a fresh code in the server. Your previous connection is retained unless enrollment completes.", InfoBarSeverity.Error);
            return;
         }
         Feedback("Device connected", result + ". Select which printers to expose here, then approve the discovered queues in the server.", InfoBarSeverity.Success);
         StatusText.Text = result;
         App.Log.Info("Print device enrollment completed.");
      }
      catch(OperationCanceledException)
      {
         Feedback("Enrollment could not be confirmed", "The request timed out. Check the device list in the server before trying again: the code may have been consumed. Revoke any unused device created by this attempt, then generate a fresh enrollment code. Your prior connection remains available unless this enrollment completed.", InfoBarSeverity.Warning);
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
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
   private async void Refresh_Click(object sender, RoutedEventArgs e) => await Task.WhenAll(RefreshAsync(), RefreshLifecycleAsync());

   private async void Reconnect_Click(object sender, RoutedEventArgs e)
   {
      if(_busy)
      {
         return;
      }
      SetBusy(true);
      try
      {
         string response = await AgentPipeClient.SendAsync(new PipeRequest("reconnect"));
         Feedback("Reconnect requested", response, InfoBarSeverity.Informational);
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or OperationCanceledException)
      {
         ServiceUnavailable();
      }
      finally
      {
         SetBusy(false);
      }
      await RefreshAsync();
   }



   /// <summary>Query diagnostic information without exposing credentials.</summary>
   private async Task RefreshAsync()
   {
      if(_busy || _refreshing)
      {
         return;
      }
      _refreshing = true;
      int mutationVersion = _mutationVersion;
      try
      {
         string response = await AgentPipeClient.SendAsync(new PipeRequest("snapshot"));
         if(_busy || mutationVersion != _mutationVersion)
         {
            return;
         }
         if(response.StartsWith("Error: ", StringComparison.Ordinal))
         {
            SetHealth("RED");
            StatusText.Text = "Local service needs attention";
            Feedback("Service reported a problem", response[7..], InfoBarSeverity.Error);
            return;
         }
         AgentSnapshot snapshot = JsonSerializer.Deserialize<AgentSnapshot>(response, Qqq.PrintAgent.Core.Printing.PrintApiClient.JsonOptions)
            ?? throw new JsonException("Missing snapshot.");
         if(!_busy)
         {
            RenderSnapshot(snapshot);
         }
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or OperationCanceledException)
      {
         ServiceUnavailable();
      }
      catch(Exception exception) when(exception is JsonException or InvalidOperationException or KeyNotFoundException)
      {
         SetHealth("RED");
         Feedback("Cannot read service response", "Install matching versions of print agent companion and service, then refresh.", InfoBarSeverity.Error);
      }
      finally
      {
         _refreshing = false;
      }
   }



   /// <summary>Give actionable guidance without logging pipe payloads or secrets.</summary>
   private void ServiceUnavailable()
   {
      SetHealth("RED");
      StatusText.Text = "Local print service unavailable";
      FooterContact.Text = "Local service unavailable · retrying automatically; shown data may be stale";
      Feedback("Service unavailable", (_serviceStatus?.Guidance ?? "The local service could not be reached.") + " Open Settings for service controls, Windows Services or the installer. This administration window needs administrator access.", InfoBarSeverity.Error);
   }



   /// <summary>Prevent overlapping local administration requests.</summary>
   private void SetBusy(bool busy)
   {
      if(busy)
      {
         _mutationVersion++;
      }
      _busy = busy;
      EnrollButton.IsEnabled = !busy;
      RefreshButton.IsEnabled = !busy;
      ReconnectButton.IsEnabled = !busy;
      foreach(PrinterRowView row in _printerRows.Values)
      {
         row.SetBusy(busy);
      }
      UpdateSelectedPrinter();
      UpdateSelectedJob();
   }



   /// <summary>Present the latest operation's result.</summary>
   private void Feedback(string title, string message, InfoBarSeverity severity)
   {
      FeedbackBar.Title = title;
      FeedbackBar.Message = message;
      FeedbackBar.Severity = severity;
      FeedbackBar.IsOpen = true;
      ConnectionFeedback.Title = title;
      ConnectionFeedback.Message = message;
      ConnectionFeedback.Severity = severity;
      ConnectionFeedback.IsOpen = true;
   }








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
      if(AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
      {
         presenter.Restore();
      }
      Activate();
   }



   /// <summary>Render the service's authoritative health, queues, jobs and bounded logs.</summary>
   private void RenderSnapshot(AgentSnapshot snapshot)
   {
      SetHealth(snapshot.Health);
      ConnectionButton.Content = snapshot.Enrolled ? "Sign in again" : "Connect this device";
      ConnectionDialog.Title = snapshot.Enrolled ? "Sign in again" : "Connect to the server";
      FooterContact.Text = $"Last server contact: {FormatTime(snapshot.LastSuccessfulPoll)} · refreshes every 5 seconds";
      StatusText.Text = snapshot.Status;
      ConnectionText.Text = snapshot.Enrolled
         ? $"Server: {snapshot.ServerUrl}\nDevice: {snapshot.DeviceId}\nLast successful poll: {FormatTime(snapshot.LastSuccessfulPoll)}"
         : "This device has not been enrolled. Open Connection settings to connect it to the server.";
      if(string.IsNullOrWhiteSpace(ServerBox.Text) && snapshot.ServerUrl is not null)
      {
         ServerBox.Text = snapshot.ServerUrl;
      }
      UpdatePrinters(snapshot);
      UpdateDeviceProfile(snapshot);
      _ = RefreshJobPageAsync();
      if(FeedbackBar.Title is "Checking service" or "Service unavailable" or "Cannot read service response")
      {
         FeedbackBar.IsOpen = false;
      }
   }



   private void UpdatePrinters(AgentSnapshot snapshot)
   {
      var present = snapshot.Printers.Select(printer => printer.PrinterId).ToHashSet(StringComparer.Ordinal);
      foreach(string id in _printerRows.Keys.Where(id => !present.Contains(id)).ToArray())
      {
         PrinterRowView removed = _printerRows[id];
         PrintersList.Items.Remove(removed);
         _printerRows.Remove(id);
      }
      foreach(PrinterSnapshot printer in snapshot.Printers)
      {
         if(!_printerRows.TryGetValue(printer.PrinterId, out PrinterRowView? row))
         {
            row = new PrinterRowView(SetExposureAsync);
            _printerRows.Add(printer.PrinterId, row);
            PrintersList.Items.Add(row);
         }
         row.Update(printer, FriendlyState(printer.Status), _busy);
      }
      PrintersEmpty.Visibility = snapshot.Printers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
      PrintersEmptyText.Text = snapshot.Enrolled ? "Add a printer in Windows Settings, then refresh this list." : "Connect this device to the server, then choose the printers to share.";
      PrintersSummary.Text = $"{snapshot.Printers.Count} printers · {snapshot.Printers.Count(printer => printer.Exposed)} shared with the server";
      UpdateSelectedPrinter();
   }

   /// <summary>Persist local printer exposure through the protected service pipe.</summary>
   private async Task SetExposureAsync(PrinterSnapshot printer, bool exposed)
   {
      if(_busy)
      {
         return;
      }
      SetBusy(true);
      try
      {
         string response = await AgentPipeClient.SendAsync(new PipeRequest("set-exposure", PrinterId: printer.PrinterId, Exposed: exposed));
         AgentSnapshot snapshot = JsonSerializer.Deserialize<AgentSnapshot>(response, Qqq.PrintAgent.Core.Printing.PrintApiClient.JsonOptions)
            ?? throw new JsonException("Missing snapshot.");
         RenderSnapshot(snapshot);
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException)
      {
         if(_printerRows.TryGetValue(printer.PrinterId, out PrinterRowView? row))
         {
            row.Exposure.IsChecked = printer.Exposed;
         }
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
      string label = health switch { "GREEN" => "Connected and ready", "ORANGE" => "Connected · printers need attention", _ => "Disconnected or needs attention" };
      FooterStatus.Text = label;
      _trayStatus.Text = label;
      _tray.ToolTipText = TrayTooltip(label);
      if(string.Equals(_currentHealth, health, StringComparison.Ordinal))
      {
         return;
      }
      Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(FooterStatus)?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
      global::Windows.UI.Color color = health switch { "GREEN" => Microsoft.UI.Colors.LimeGreen, "ORANGE" => Microsoft.UI.Colors.Orange, _ => Microsoft.UI.Colors.Red };
      SolidColorBrush brush = new(color);
      FooterDot.Fill = brush;
      string icon = health switch { "GREEN" => "green", "ORANGE" => "orange", _ => "red" };
      if(!_trayIcons.TryGetValue(icon, out System.Drawing.Icon? trayIcon))
      {
         trayIcon = LoadTrayIcon(icon);
         _trayIcons.Add(icon, trayIcon);
      }
      // H.NotifyIcon owns and disposes values assigned to Icon. These cached icons
      // remain ours; UpdateIcon sends their native handle without transferring ownership.
      if(!_tray.UpdateIcon(trayIcon) || _tray.TrayIcon.Icon != trayIcon.Handle)
      {
         App.Log.Warning("[Companion] Windows did not accept the tray status icon; the next refresh will retry.");
         return;
      }
      _currentHealth = health;
   }

   private static string TrayTooltip(string status)
   {
      string text = status + " — " + AppIdentity.Info.DisplayName;
      // Windows notification icons allow 127 UTF-16 characters plus the terminator.
      if(text.Length <= 127)
      {
         return text;
      }
      int length = char.IsHighSurrogate(text[125]) ? 125 : 126;
      return text[..length] + "…";
   }

   /// <summary>Load the packaged status image synchronously before registering the tray icon.</summary>
   private static System.Drawing.Icon LoadTrayIcon(string state)
   {
      byte[] png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", $"tray-{state}.png"));
      using var stream = new MemoryStream();
      using(var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
      {
         // One PNG-backed 64px ICO image; avoids asynchronous WinUI ImageSource decoding.
         writer.Write((ushort)0);
         writer.Write((ushort)1);
         writer.Write((ushort)1);
         writer.Write((byte)64);
         writer.Write((byte)64);
         writer.Write((ushort)0);
         writer.Write((ushort)1);
         writer.Write((ushort)32);
         writer.Write(png.Length);
         writer.Write(22);
         writer.Write(png);
      }
      stream.Position = 0;
      return new System.Drawing.Icon(stream);
   }



   /// <summary>Apply and save the AppKit theme.</summary>
   private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
   {
      if(_initializing || ThemeChoice.SelectedItem is not ComboBoxItem item)
      {
         return;
      }
      _settings.Theme = (string)item.Tag;
      WinUIShell.ApplyTheme(RootGrid, _settings.ThemeKind);
      Save();
   }



   /// <summary>Save the splash preference.</summary>
   private void Splash_Toggled(object sender, RoutedEventArgs e)
   {
      if(_initializing)
      {
         return;
      }
      _settings.ShowSplashScreen = SplashToggle.IsOn;
      SplashDuration.IsEnabled = _settings.ShowSplashScreen;
      Save();
   }



   /// <summary>Show the shared AppKit activity log.</summary>
   private void ActivityLog_Click(object sender, RoutedEventArgs e) => LogWindow.ShowSingle(App.Log, AppIdentity.Info);



   /// <summary>Show the shared AppKit About dialog.</summary>
   private async void About_Click(object sender, RoutedEventArgs e)
   {
      var dialog = new AboutDialog(AppIdentity.Info) { XamlRoot = Content.XamlRoot };
      if(dialog.Content is ScrollViewer { Content: StackPanel body })
      {
         if(AppIdentity.Branding.SupportUrl is Uri support)
         {
            body.Children.Add(new HyperlinkButton { Content = "Contact support", NavigateUri = support });
         }
         if(CustomerLogo.Visibility == Visibility.Visible)
         {
            body.Children.Insert(0, new Image { Source = CustomerLogo.Source, Width = 128, Height = 64, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left });
         }
      }
      await DialogGuard.ShowAsync(dialog);
   }

   /// <summary>Use the single AppKit log viewer from every logs entry point.</summary>
   private void ServiceLogs_Click(object sender, RoutedEventArgs e)
   {
      Navigation.SelectedItem = LogsNavigation;
      LogWindow.ShowSingle(App.Log, AppIdentity.Info);
   }


   private static string FormatTime(string? timestamp)
      => DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTimeOffset time)
         ? time.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) : "Not yet";

   private static string FriendlyState(string state) => state switch
   {
      "READY" => "Ready",
      "OFFLINE" => "Offline",
      "PAUSED" => "Paused",
      "ERROR" => "Needs attention",
      "QUEUED" => "Queued",
      "CLAIMED" => "Assigned to this computer",
      "RECEIVED" => "Received",
      "SUBMITTING" => "Sending to printer",
      "SPOOLED" => "Sent to printer",
      "SPOOLER_COMPLETED" => "Windows completed the job",
      "FAILED" => "Failed",
      "OUTCOME_UNKNOWN" => "Check printer before retrying",
      _ => "Status unavailable",
   };

   /// <summary>Persist presentation preferences only.</summary>
   private void Save()
   {
      if(!AppIdentity.SettingsStore.Save(_settings))
      {
         Feedback("Settings not saved", "The window preference could not be saved.", InfoBarSeverity.Warning);
      }
   }
}

