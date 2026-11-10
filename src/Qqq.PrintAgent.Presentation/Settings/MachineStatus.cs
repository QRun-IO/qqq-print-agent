/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Settings;

/// <summary>The machine-level texts of the Settings page: this computer's own labels, the Windows service state, and what to say when Windows cannot be asked.</summary>
public static class MachineStatus
{
   /// <summary>The service state line when the Windows service could not be queried.</summary>
   public const string ServiceUnknown = "Service status unavailable";

   /// <summary>What to do when the Windows service could not be queried.</summary>
   public const string ServiceUnknownGuidance = "Open Windows Services to inspect the service, then refresh.";

   /// <summary>The startup line when the sign-in task could not be read; it says the preference itself was left alone.</summary>
   public const string StartupUnknownGuidance = "Startup status could not be read. The preference was not changed.";



   /// <summary>The confirmation for restarting the service: a job being submitted could be left with an uncertain outcome.</summary>
   public static ConfirmationPrompt RestartPrompt { get; } = new("Restart the print service?", "A job being submitted could have an uncertain outcome. Wait for active jobs to finish when possible.", "Restart service");



   /// <summary>The computer's name, location and description as one block, using the Windows computer name when the operator has not set a name.</summary>
   public static string DeviceProfile(DeviceProfile profile, string machineName)
   {
      ArgumentNullException.ThrowIfNull(profile);
      string name = string.IsNullOrWhiteSpace(profile.Name) ? machineName : profile.Name;
      return $"{name}\nLocation: {profile.Location ?? "Not set"}\n{profile.Description}";
   }



   /// <summary>The Windows service state in words, or that the service is not installed.</summary>
   public static string ServiceState(AgentServiceStatus status)
   {
      ArgumentNullException.ThrowIfNull(status);
      return status.Installed ? $"Windows service: {status.State}" : "Windows service is not installed";
   }
}
