/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using KofTwentyTwo.AppKit;
using KofTwentyTwo.AppKit.Settings;
using KofTwentyTwo.AppKit.Updates;
using KofTwentyTwo.AppKit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Qqq.PrintAgent.Windows;


namespace Qqq.PrintAgent;

public sealed partial class MainWindow
{
   private readonly CancellationTokenSource _updateLifetime = new();
   private MsiUpdateService? _updateService;
   private UpdateCoordinator? _updates;
   private bool _checkingUpdates;
   private bool _startupUpdateScheduled;
   private bool _updatesClosed;

   private void InitializeAppKitSettings()
   {
      SplashToggle.IsOn = _settings.ShowSplashScreen;
      SplashDuration.Minimum = ShellSettings.MinSplashMilliseconds;
      SplashDuration.Maximum = ShellSettings.MaxSplashMilliseconds;
      SplashDuration.Value = _settings.SplashMilliseconds;
      SplashDuration.IsEnabled = _settings.ShowSplashScreen;
      ThemeChoice.SelectedIndex = _settings.ThemeKind switch { AppTheme.Light => 1, AppTheme.Dark => 2, _ => 0 };
      Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(ThemeChoice, "AppTheme");
      UpdateStartupToggle.IsOn = _settings.CheckForUpdatesOnStartup;
      _updateService = new MsiUpdateService(() => DispatcherQueue.TryEnqueue(ExitCompanion), App.Log);
      _updateService.StatusChanged += UpdateService_StatusChanged;
      var prompter = new LifetimeUserPrompter(new ContentDialogPrompter(UpdateDialogRoot), _updateLifetime.Token);
      _updates = new UpdateCoordinator(_updateService, prompter, AppIdentity.Info.DisplayName, App.Log);
      VersionText.Text = "Version " + BuildVersion.Describe(typeof(MainWindow).Assembly);
      UpdateChannelText.Text = BuildVersion.IsPrerelease(typeof(MainWindow).Assembly) ? "Update channel: prerelease" : "Update channel: stable";
      UpdateStatusText.Text = _updateService.IsSupported
         ? "Updates install the companion and print service together. You choose when to install."
         : "Install the machine MSI to enable in-app updates. Release downloads remain available below.";
      CheckUpdatesButton.IsEnabled = _updateService.IsSupported;
      CheckUpdatesMenuItem.IsEnabled = _updateService.IsSupported;
      string? outcome = MsiUpdateInstaller.ReadLastOutcomeMessage();
      LastUpdateOutcomeText.Text = outcome ?? string.Empty;
      LastUpdateOutcomeText.Visibility = string.IsNullOrWhiteSpace(outcome) ? Visibility.Collapsed : Visibility.Visible;
   }

   private XamlRoot UpdateDialogRoot()
   {
      if(_updatesClosed)
      {
         throw new OperationCanceledException("The companion window has closed.");
      }
      ShowDetails();
      return Content.XamlRoot;
   }

   private void UpdateService_StatusChanged(object? sender, UpdateStatusEventArgs args)
   {
      DispatcherQueue.TryEnqueue(() =>
      {
         if(!_updatesClosed)
         {
            UpdateStatusText.Text = args.Message;
         }
      });
   }

   private async Task CheckUpdatesAfterStartupAsync()
   {
      if(_startupUpdateScheduled)
      {
         return;
      }
      _startupUpdateScheduled = true;
      if(!_settings.CheckForUpdatesOnStartup)
      {
         return;
      }
      try
      {
         await Task.Delay(_settings.ShowSplashScreen ? _settings.SplashMilliseconds + 500 : 1000, _updateLifetime.Token);
         if(_settings.CheckForUpdatesOnStartup)
         {
            await CheckUpdatesAsync(interactive: false);
         }
      }
      catch(OperationCanceledException) when(_updatesClosed)
      {
         // Closing the companion cancels its optional startup check.
      }
   }

   private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(interactive: true);

   private async Task CheckUpdatesAsync(bool interactive)
   {
      if(_checkingUpdates || _updatesClosed || _updates is null || _updateService?.IsSupported != true)
      {
         return;
      }
      _checkingUpdates = true;
      CheckUpdatesButton.IsEnabled = false;
      CheckUpdatesMenuItem.IsEnabled = false;
      UpdateStatusText.Text = "Checking for updates…";
      try
      {
         if(interactive)
         {
            await _updates.CheckInteractivelyAsync(_updateLifetime.Token);
         }
         else
         {
            await _updates.CheckQuietlyAsync(_updateLifetime.Token);
         }
      }
      catch(OperationCanceledException) when(_updatesClosed)
      {
         // Window shutdown owns cancellation; no dialog should outlive it.
      }
      catch(Exception exception)
      {
         App.Log.Error("[Companion] Update check could not finish.", exception);
         if(interactive && !_updatesClosed)
         {
            Feedback("Update check unavailable", "Open Logs for details, or use Release notes and downloads below.", InfoBarSeverity.Warning);
         }
      }
      finally
      {
         _checkingUpdates = false;
         if(!_updatesClosed)
         {
            CheckUpdatesButton.IsEnabled = _updateService?.IsSupported == true;
            CheckUpdatesMenuItem.IsEnabled = _updateService?.IsSupported == true;
            UpdateStatusText.Text = "Update check finished. Installation always requires your confirmation. See Logs for check details.";
         }
      }
   }

   private void UpdateStartup_Toggled(object sender, RoutedEventArgs e)
   {
      if(_initializing)
      {
         return;
      }
      _settings.CheckForUpdatesOnStartup = UpdateStartupToggle.IsOn;
      Save();
   }

   private void DisposeUpdates()
   {
      _updatesClosed = true;
      _updateLifetime.Cancel();
      if(_updateService is not null)
      {
         _updateService.StatusChanged -= UpdateService_StatusChanged;
      }
      _updateService?.Dispose();
      _updateLifetime.Dispose();
   }

   private void SplashDuration_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
   {
      if(_initializing || double.IsNaN(args.NewValue))
      {
         return;
      }
      _settings.SplashMilliseconds = (int)Math.Clamp(args.NewValue, ShellSettings.MinSplashMilliseconds, ShellSettings.MaxSplashMilliseconds);
      Save();
   }

   private void SettingsAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
   {
      Navigation.SelectedItem = Navigation.SettingsItem;
      args.Handled = true;
   }

   private void LogsAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
   {
      Navigation.SelectedItem = LogsNavigation;
      LogWindow.ShowSingle(App.Log, AppIdentity.Info);
      args.Handled = true;
   }
}
