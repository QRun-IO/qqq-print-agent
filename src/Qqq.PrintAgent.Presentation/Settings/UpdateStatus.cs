/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Settings;

/// <summary>The update texts on the Settings page and the timing of the optional startup check. Nothing here installs anything: installation always needs the operator's confirmation in the update dialog.</summary>
public static class UpdateStatus
{
   /// <summary>The status line while a check is running.</summary>
   public const string Checking = "Checking for updates…";

   /// <summary>The status line after a check, whatever it found; the details are in the log.</summary>
   public const string Finished = "Update check finished. Installation always requires your confirmation. See Logs for check details.";



   /// <summary>The message when an interactive check could not finish; a quiet startup check stays quiet.</summary>
   public static FeedbackMessage CheckUnavailable { get; } = new("Update check unavailable", "Open Logs for details, or use Release notes and downloads below.", FeedbackSeverity.Warning);



   /// <summary>Which release channel this build follows.</summary>
   public static string Channel(bool prerelease) => prerelease ? "Update channel: prerelease" : "Update channel: stable";



   /// <summary>What in-app updates do when they are available, or why they are not: only a machine MSI installation can update itself.</summary>
   public static string Availability(bool supported) => supported
      ? "Updates install the companion and print service together. You choose when to install."
      : "Install the machine MSI to enable in-app updates. Release downloads remain available below.";



   /// <summary>How long the quiet startup check waits: half a second after the splash would end, or one second when there is no splash, so the check never competes with startup.</summary>
   public static TimeSpan StartupDelay(bool showSplash, int splashMilliseconds) => TimeSpan.FromMilliseconds(showSplash ? splashMilliseconds + 500 : 1000);
}
