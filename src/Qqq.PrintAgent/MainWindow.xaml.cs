/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using KofTwentyTwo.AppKit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Qqq.PrintAgent.Presentation;
using Qqq.PrintAgent.Presentation.Printers;
using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Windows;
using PrintersSummaryText = Qqq.PrintAgent.Presentation.Printers.PrintersSummary;


namespace Qqq.PrintAgent;

/// <summary>AppKit companion for enrollment and diagnostics of the machine print service.</summary>
public sealed partial class MainWindow : Window, IDisposable
{
   private readonly ServiceClient _service;
   private readonly TimestampFormat _time = new();
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
   public MainWindow() : this(AgentPipeClient.SendReportingPhaseAsync)
   {
   }



   /// <summary>Allows the native regression host to use synthetic service responses without printing.</summary>
   internal MainWindow(Func<PipeRequest, Task<string>> sendRequest)
   {
      CompanionLog log = new();
      _service = new ServiceClient(sendRequest ?? throw new ArgumentNullException(nameof(sendRequest)), log, AppIdentity.ServerName);
      _signIn = new SignInFlow(_service, new QqqSignInSessionFactory(), new DefaultBrowserLauncher(), new BrowserCallbackSource(), log);
      InitializeComponent();
      _signIn.PropertyChanged += SignIn_PropertyChanged;
      ApplyBranding();
      this.ResizeForDpi(1100, 760);
      this.SetMinimumSize(800, 560);
      this.SetAppIcon(AppIdentity.Info);
      InitializeAppKitSettings();
      Navigation.SelectedItem = PrintersNavigation;
      Navigation.Loaded += Navigation_Loaded;
      _initializing = false;
      SplashOverlay.ShowOver(RootGrid, AppIdentity.Info, _settings);
      // H.NotifyIcon draws the tray menu as a native Windows menu built from these items, so they are never
      // elements on screen and carry no automation identifier (the one counted exception in XamlContractTests).
      _trayStatus = new MenuFlyoutItem { Text = "Red — service not connected", Command = new RelayCommand(ShowDetails) };
      MenuFlyout trayMenu = new();
      trayMenu.Items.Add(_trayStatus);
      trayMenu.Items.Add(new MenuFlyoutItem { Text = "Open details", Command = new RelayCommand(ShowDetails) });
      trayMenu.Items.Add(new MenuFlyoutItem { Text = "Refresh status", Command = new AsyncRelayCommand(RefreshAsync) });
      trayMenu.Items.Add(new MenuFlyoutSeparator());
      trayMenu.Items.Add(new MenuFlyoutItem { Text = "Exit companion (printing continues)", Command = new RelayCommand(ExitCompanion) });
      _tray = new TaskbarIcon { ToolTipText = HealthStatus.TrayToolTip("Service not connected", AppIdentity.Info.DisplayName), ContextFlyout = trayMenu, LeftClickCommand = new RelayCommand(ShowDetails) };
      SetHealth("RED");
      _tray.ForceCreate(enablesEfficiencyMode: false);
      App.Log.Info($"Tray icon created: {_tray.IsCreated}.");
      _refreshTimer.Tick += async (_, _) => await RefreshAsync();
      _refreshTimer.Start();
      AppWindow.Closing += (_, args) =>
      {
         InvalidateJobDetails();
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



   /// <summary>
   /// Applies the customer's runtime branding to the window: product and company headings, the About button label, the
   /// support link (falling back to the project's issue tracker) and an optional logo. A logo that fails to load is hidden
   /// rather than shown broken, so a bad branding file can never block the window.
   /// </summary>
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
      if((AppIdentity.Branding.LogoPath ?? AppIdentity.EditionLogoPath) is string logo)
      {
         CustomerLogo.ImageFailed += (_, _) => CustomerLogo.Visibility = Visibility.Collapsed;
         CustomerLogo.Source = new BitmapImage(new Uri(logo));
         CustomerLogo.Visibility = Visibility.Visible;
      }
   }



   /// <summary>Release the tray icon and polling timer when the companion exits.</summary>
   public void Dispose()
   {
      _signIn.Dispose();
      if(_updatesClosed)
      {
         return;
      }
      InvalidateJobDetails();
      DisposeUpdates();
      _refreshTimer.Stop();
      _transportTimer.Stop();
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
      InitializeTransportStatus();
      await Task.WhenAll(RefreshAsync(), RefreshLifecycleAsync(), CheckUpdatesAfterStartupAsync(), RefreshTransportAsync());
   }



   /// <summary>
   /// Labels the navigation's built-in Settings item once the template has created it, which only happens after
   /// construction: an automation identifier for tests and assistive technology, an access key and a tooltip with the shortcut.
   /// </summary>
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



   /// <summary>
   /// Shows exactly one page (Printers, Jobs, Logs or Settings) and updates its title and description. Any open job details
   /// are invalidated first so a stale detail view cannot outlive its page. Logs opens the single shared log viewer, and
   /// Settings and Jobs refresh their data on entry, but not during startup, when the window refreshes everything itself.
   /// </summary>
   private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
   {
      if(PageTitle is null)
      {
         return;
      }
      string page = ShellPages.Name(args.IsSettingsSelected, (args.SelectedItem as NavigationViewItem)?.Tag as string);
      InvalidateJobDetails();
      PrintersPage.Visibility = string.Equals(page, ShellPages.Printers, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
      JobsPage.Visibility = string.Equals(page, ShellPages.Jobs, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
      LogsPage.Visibility = string.Equals(page, ShellPages.Logs, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
      SettingsPage.Visibility = string.Equals(page, ShellPages.Settings, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
      PageTitle.Text = page;
      PageDescription.Text = ShellPages.Description(page);
      if(string.Equals(page, ShellPages.Logs, StringComparison.Ordinal))
      {
         ShowActivityLog();
      }
      if(args.IsSettingsSelected && !_initializing)
      {
         await RefreshLifecycleAsync();
      }
      if(string.Equals(page, ShellPages.Jobs, StringComparison.Ordinal) && !_initializing)
      {
         await RefreshJobPageAsync();
      }
   }



   /// <summary>Opens the connection dialog from its button; the work is in <c>ShowConnectionAsync</c> so other entry points can reuse it.</summary>
   private async void Connection_Click(object sender, RoutedEventArgs e) => await ShowConnectionAsync();



   /// <summary>Shows the connection dialog through the dialog guard, which serializes dialogs so two can never be open at once.</summary>
   private async Task ShowConnectionAsync()
   {
      ConnectionDialog.XamlRoot = Content.XamlRoot;
      await LoadServerTrustAsync();
      await DialogGuard.ShowAsync(ConnectionDialog);
   }



   /// <summary>Refresh the local service and the queues visible under its identity.</summary>
   private async void Refresh_Click(object sender, RoutedEventArgs e) => await Task.WhenAll(RefreshAsync(), RefreshLifecycleAsync());



   /// <summary>
   /// Asks the service to reconnect to the server now. It ignores the click while another administration request is
   /// running, reports the service's answer, and refreshes so the footer shows the result rather than the old state.
   /// </summary>
   private async void Reconnect_Click(object sender, RoutedEventArgs e)
   {
      if(_busy)
      {
         return;
      }
      SetBusy(true);
      try
      {
         ServiceResult<string> result = await _service.ReconnectAsync();
         if(result.Reply is string reply)
         {
            Feedback(ShellFeedback.ReconnectRequested(reply));
         }
         else
         {
            ServiceUnavailable();
         }
      }
      finally
      {
         SetBusy(false);
      }
      await RefreshAsync();
   }



   /// <summary>
   /// Query diagnostic information without exposing credentials. Overlapping refreshes are skipped, and a reply that arrived
   /// after an administration request started is dropped, because that request will render a newer state itself. Anything in
   /// the request or in rendering its answer that fails as an invalid operation or a missing key is shown as a status this
   /// version cannot read, with a red footer, and never leaves this method: the timer, the Refresh button, the tray command
   /// and the window's start all call it without a handler of their own.
   /// </summary>
   private async Task RefreshAsync()
   {
      if(_updatesClosed || _busy || _refreshing)
      {
         return;
      }
      _refreshing = true;
      int mutationVersion = _mutationVersion;
      try
      {
         ServiceResult<AgentSnapshot> result = await _service.SnapshotAsync();
         bool replied = result.Outcome != ServiceOutcome.Unavailable || result.Failure == ServiceFailure.UnreadableReply;
         if(replied && (_updatesClosed || _busy || mutationVersion != _mutationVersion))
         {
            return;
         }
         ApplyStatus(result);
         if(result.Succeeded)
         {
            // The service is answering: a certificate exception whose save could not be confirmed is read again now.
            await _signIn.ReadUnconfirmedTrustAsync();
         }
      }
      catch(Exception exception) when(exception is InvalidOperationException or KeyNotFoundException)
      {
         SetHealth("RED");
         Feedback(ShellFeedback.UnreadableStatus);
      }
      finally
      {
         _refreshing = false;
      }
   }



   /// <summary>
   /// Shows the outcome of a status request: the snapshot, the service's own error, a reply this version cannot read (which
   /// means mismatched versions), or an unreachable service.
   /// </summary>
   private void ApplyStatus(ServiceResult<AgentSnapshot> result)
   {
      if(result.Outcome == ServiceOutcome.Rejected)
      {
         SetHealth("RED");
         StatusText.Text = ShellStatus.ServiceProblemStatus;
         Feedback(ShellFeedback.ServiceProblem(result.Message));
         return;
      }
      if(!result.Succeeded)
      {
         if(result.Failure == ServiceFailure.UnreadableReply)
         {
            SetHealth("RED");
            Feedback(ShellFeedback.UnreadableStatus);
            return;
         }
         ServiceUnavailable();
         return;
      }
      RenderSnapshot(result.Payload);
   }



   /// <summary>Give actionable guidance without logging pipe payloads or secrets.</summary>
   private void ServiceUnavailable()
   {
      if(_updatesClosed)
      {
         return;
      }
      SetHealth("RED");
      StatusText.Text = ShellStatus.ServiceUnavailableStatus;
      FooterContact.Text = ShellStatus.ServiceUnavailableContact;
      RenderTransport(null);
      Feedback(ShellFeedback.ServiceUnavailable(_serviceStatus?.Guidance));
   }



   /// <summary>
   /// Prevent overlapping local administration requests. This one flag covers the whole window; busy state per server
   /// belongs to the multi-server work and is deliberately not attempted here.
   /// </summary>
   private void SetBusy(bool busy)
   {
      if(_updatesClosed)
      {
         return;
      }
      if(busy)
      {
         _mutationVersion++;
         InvalidateJobDetails();
      }
      _busy = busy;
      JobsPrevious.IsEnabled = _jobs.CanGoPrevious(busy);
      JobsNext.IsEnabled = _jobs.CanGoNext(busy);
      SetTableHeadersEnabled(!busy);
      PairingChoice.IsEnabled = !busy;
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
      if(_updatesClosed)
      {
         return;
      }
      FeedbackBar.Title = title;
      FeedbackBar.Message = message;
      FeedbackBar.Severity = severity;
      FeedbackBar.IsOpen = true;
      ConnectionFeedback.Title = title;
      ConnectionFeedback.Message = message;
      ConnectionFeedback.Severity = severity;
      ConnectionFeedback.IsOpen = true;
   }



   /// <summary>Presents a message prepared by the presentation assembly, mapping its severity to the message bar's.</summary>
   private void Feedback(FeedbackMessage message) => Feedback(message.Title, message.Message, message.Severity switch
   {
      FeedbackSeverity.Success => InfoBarSeverity.Success,
      FeedbackSeverity.Warning => InfoBarSeverity.Warning,
      FeedbackSeverity.Error => InfoBarSeverity.Error,
      _ => InfoBarSeverity.Informational,
   });



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
      if(_updatesClosed)
      {
         return;
      }
      AppWindow.Show();
      if(AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
      {
         presenter.Restore();
      }
      Activate();
   }



   /// <summary>
   /// Renders the service's authoritative health, connection texts and printers. The texts are worked out by
   /// <see cref="ShellStatus"/>; this method copies them into the footer and the Settings page, fills the server box the
   /// first time an address is known, and closes a service-level message that the good status made obsolete.
   /// </summary>
   private void RenderSnapshot(AgentSnapshot snapshot)
   {
      if(_updatesClosed)
      {
         return;
      }
      SetHealth(snapshot.Health);
      RenderTransport(snapshot.Transport);
      var status = ShellStatus.From(snapshot, _time, AppIdentity.ServerName);
      FooterServerLabel.Text = status.ServerLabel;
      ToolTipService.SetToolTip(FooterServer, status.ServerLinkName);
      Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(FooterServer, status.ServerLinkName);
      ConnectionButton.Content = status.ConnectionAction;
      ConnectionDialog.Title = status.ConnectionDialogTitle;
      FooterContact.Text = status.FooterContact;
      StatusText.Text = snapshot.Status;
      FooterStatus.Text = status.FooterStatus;
      ConnectionText.Text = status.ConnectionText;
      AuthenticationText.Text = status.AuthenticationText;
      ToolTipService.SetToolTip(FooterStatus, FooterStatus.Text);
      ToolTipService.SetToolTip(FooterContact, FooterContact.Text);
      if(string.IsNullOrWhiteSpace(ServerBox.Text) && snapshot.ServerUrl is not null)
      {
         ServerBox.Text = snapshot.ServerUrl;
      }
      UpdatePrinters(snapshot);
      UpdateDeviceProfile(snapshot);
      _ = RefreshJobPageAsync();
      if(ShellFeedback.ClearsOnStatus(FeedbackBar.Title))
      {
         FeedbackBar.IsOpen = false;
      }
   }



   /// <summary>
   /// Reconciles the printer list with the service's snapshot: rows for printers that disappeared are removed, new printers
   /// get a row, and existing rows are updated in place so selection and expansion survive a refresh. The empty-state text
   /// and the shared-printer count are recomputed each time.
   /// </summary>
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
            row = new PrinterRowView(SetExposureAsync, EditPrinterAsync, TestPrintAsync, TogglePrinterDetails);
            _printerRows.Add(printer.PrinterId, row);
            PrintersList.Items.Add(row);
         }
         row.Update(printer, PrinterRow.From(printer, snapshot.PrinterSync, AppIdentity.ServerName), _busy);
      }
      SortPrinterRows();
      PrintersEmpty.Visibility = snapshot.Printers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
      PrintersEmptyText.Text = PrintersSummaryText.EmptyText(snapshot.Enrolled);
      PrintersSummary.Text = PrintersSummaryText.Text(snapshot, AppIdentity.ServerName);
      ToolTipService.SetToolTip(PrintersSummary, snapshot.PrinterSync?.Detail);
      UpdateSelectedPrinter();
   }



   /// <summary>
   /// Persists local printer sharing through the service and shows the snapshot it returns. When the service does not
   /// confirm the change, the checkbox goes back to what the service last reported, so the list never shows a choice that was not saved.
   /// </summary>
   private async Task SetExposureAsync(PrinterSnapshot printer, bool exposed)
   {
      if(_busy)
      {
         return;
      }
      SetBusy(true);
      try
      {
         ServiceResult<AgentSnapshot> result = await _service.SetExposureAsync(printer.PrinterId, exposed);
         if(result.Succeeded)
         {
            RenderSnapshot(result.Payload);
            return;
         }
         if(_printerRows.TryGetValue(printer.PrinterId, out PrinterRowView? row))
         {
            row.Exposure.IsChecked = printer.Exposed;
         }
         Feedback(ShellFeedback.ExposureNotSaved);
      }
      finally
      {
         SetBusy(false);
      }
   }



   /// <summary>
   /// Pairs the tray color with a text status so color is never the only signal. The label, level and icon name come from
   /// <see cref="HealthStatus"/>; this method puts them on the footer and the tray, and changes the dot and the tray icon
   /// only when the health word itself changed.
   /// </summary>
   private void SetHealth(string health)
   {
      if(_updatesClosed)
      {
         return;
      }
      var status = HealthStatus.From(health);
      FooterStatus.Text = status.Label;
      _trayStatus.Text = status.Label;
      _tray.ToolTipText = HealthStatus.TrayToolTip(status.Label, AppIdentity.Info.DisplayName);
      if(string.Equals(_currentHealth, health, StringComparison.Ordinal))
      {
         return;
      }
      Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(FooterStatus)?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
      global::Windows.UI.Color color = status.Level switch { HealthLevel.Green => Microsoft.UI.Colors.LimeGreen, HealthLevel.Orange => Microsoft.UI.Colors.Orange, _ => Microsoft.UI.Colors.Red };
      SolidColorBrush brush = new(color);
      FooterDot.Fill = brush;
      if(!_trayIcons.TryGetValue(status.IconName, out System.Drawing.Icon? trayIcon))
      {
         trayIcon = LoadTrayIcon(status.IconName);
         _trayIcons.Add(status.IconName, trayIcon);
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
   private void ActivityLog_Click(object sender, RoutedEventArgs e) => ShowActivityLog();



   /// <summary>Show the shared AppKit About dialog.</summary>
   private async void About_Click(object sender, RoutedEventArgs e)
   {
      var dialog = new AboutDialog(AppIdentity.Info) { XamlRoot = Content.XamlRoot };
      if(dialog.Content is ScrollViewer { Content: StackPanel body })
      {
         AboutContent.Compose(body, AppIdentity.Branding.SupportUrl, CustomerLogo.Visibility == Visibility.Visible ? CustomerLogo.Source as BitmapImage : null);
      }
      await DialogGuard.ShowAsync(dialog);
   }



   /// <summary>Use the single AppKit log viewer from every logs entry point.</summary>
   private void ServiceLogs_Click(object sender, RoutedEventArgs e)
   {
      Navigation.SelectedItem = LogsNavigation;
      ShowActivityLog();
   }



   /// <summary>Persist presentation preferences only.</summary>
   private void Save()
   {
      if(!AppIdentity.SettingsStore.Save(_settings))
      {
         Feedback("Settings not saved", "The window preference could not be saved.", InfoBarSeverity.Warning);
      }
   }
}

