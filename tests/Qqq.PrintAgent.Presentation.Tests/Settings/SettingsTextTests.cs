/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Presentation.Settings;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.Settings;

/// <summary>The Settings page texts: updates, diagnostic log settings, this computer's labels and the Windows service state.</summary>
public sealed class SettingsTextTests
{
   /// <summary>The update texts name the channel, say what updates do or why they are unavailable, and report progress in fixed words.</summary>
   [Fact]
   public void UpdateTexts_NameChannelAndAvailability()
   {
      Assert.Equal("Update channel: prerelease", UpdateStatus.Channel(prerelease: true));
      Assert.Equal("Update channel: stable", UpdateStatus.Channel(prerelease: false));
      Assert.Equal("Updates install the companion and print service together. You choose when to install.", UpdateStatus.Availability(supported: true));
      Assert.Equal("Install the machine MSI to enable in-app updates. Release downloads remain available below.", UpdateStatus.Availability(supported: false));
      Assert.Equal("Checking for updates…", UpdateStatus.Checking);
      Assert.Equal("Update check finished. Installation always requires your confirmation. See Logs for check details.", UpdateStatus.Finished);
      Assert.Equal(new FeedbackMessage("Update check unavailable", "Open Logs for details, or use Release notes and downloads below.", FeedbackSeverity.Warning), UpdateStatus.CheckUnavailable);
   }



   /// <summary>The quiet startup check waits until half a second after the splash would end, or one second when no splash is shown.</summary>
   [Fact]
   public void StartupCheck_WaitsForTheSplash()
   {
      Assert.Equal(TimeSpan.FromMilliseconds(2000), UpdateStatus.StartupDelay(showSplash: true, splashMilliseconds: 1500));
      Assert.Equal(TimeSpan.FromSeconds(1), UpdateStatus.StartupDelay(showSplash: false, splashMilliseconds: 1500));
   }



   /// <summary>The usage line shows the current file size in MiB with one decimal and how often it rolled over today.</summary>
   [Fact]
   public void LogUsage_ShowsSizeAndRollovers()
      => Assert.Equal("Current log file: 1.5 MiB; rolled over 2 time(s) today.", LogSettingsForm.Usage(new LogSettings(CurrentFileBytes: 1572864, SegmentsToday: 2)));



   /// <summary>The three number boxes are read as whole numbers with the debug switch; a blank box in any position produces nothing, so nothing is sent.</summary>
   [Fact]
   public void LogSettings_AreReadFromTheBoxesOrNotAtAll()
   {
      Assert.Equal(new LogSettings(14, 50, 5, DebugLogging: true), LogSettingsForm.Read(14, 50.9, 5, debugLogging: true));
      Assert.Null(LogSettingsForm.Read(double.NaN, 50, 5, debugLogging: false));
      Assert.Null(LogSettingsForm.Read(14, double.NaN, 5, debugLogging: false));
      Assert.Null(LogSettingsForm.Read(14, 50, double.NaN, debugLogging: false));
   }



   /// <summary>Saving is recorded in the activity log with every value, and the failure and confirmation texts are fixed.</summary>
   [Fact]
   public void LogSettingsMessages_RecordAndExplain()
   {
      Assert.Equal("Log settings saved: retention 14 days, file size 50 MiB, 5 segments per day, debug logging on.", LogSettingsForm.SavedEntry(new LogSettings(14, 50, 5, DebugLogging: true)));
      Assert.Equal("Log settings saved: retention 30 days, file size 100 MiB, 10 segments per day, debug logging off.", LogSettingsForm.SavedEntry(new LogSettings()));
      Assert.Equal(new FeedbackMessage("Log settings unavailable", "Check the local service and try again.", FeedbackSeverity.Error), LogSettingsForm.Unavailable);
      Assert.Equal(new FeedbackMessage("Log settings not saved", "Enter valid retention, file size and segment values.", FeedbackSeverity.Warning), LogSettingsForm.Invalid);
      Assert.Equal(new ConfirmationPrompt("Clear local diagnostic logs?", "This permanently deletes retained service and companion diagnostic logs. Print jobs, printer settings and job history are preserved. New events will start a fresh log.", "Clear logs"), LogSettingsForm.ClearPrompt);
   }



   /// <summary>The computer's labels use the operator's name when set and the Windows computer name otherwise, and say "Not set" for a missing location.</summary>
   [Fact]
   public void DeviceProfile_FallsBackToTheComputerName()
   {
      Assert.Equal("Packing station 2\nLocation: Dock 4\nLabels for outbound parcels.", MachineStatus.DeviceProfile(new DeviceProfile("Packing station 2", "Labels for outbound parcels.", "Dock 4"), "DESKTOP-01"));
      Assert.Equal("DESKTOP-01\nLocation: Not set\n", MachineStatus.DeviceProfile(new DeviceProfile(), "DESKTOP-01"));
   }



   /// <summary>The Windows service line shows the state or that the service is not installed; the fixed texts cover a state that could not be read and the restart confirmation.</summary>
   [Fact]
   public void ServiceState_IsSaidInWords()
   {
      Assert.Equal("Windows service: Running", MachineStatus.ServiceState(new AgentServiceStatus(true, "Running", CanStart: false, CanRestart: true, "The service is running.")));
      Assert.Equal("Windows service is not installed", MachineStatus.ServiceState(new AgentServiceStatus(false, "Missing", CanStart: false, CanRestart: false, "Install the machine MSI.")));
      Assert.Equal("Service status unavailable", MachineStatus.ServiceUnknown);
      Assert.Equal("Open Windows Services to inspect the service, then refresh.", MachineStatus.ServiceUnknownGuidance);
      Assert.Equal("Startup status could not be read. The preference was not changed.", MachineStatus.StartupUnknownGuidance);
      Assert.Equal(new ConfirmationPrompt("Restart the print service?", "A job being submitted could have an uncertain outcome. Wait for active jobs to finish when possible.", "Restart service"), MachineStatus.RestartPrompt);
   }
}
