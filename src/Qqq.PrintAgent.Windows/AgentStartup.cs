/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;


namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Explicitly opts the current administrator into an interactive sign-in task for the installed companion. This class is
/// the entry point for the installed product: it applies the rules in <see cref="AgentStartupTasks"/> to the real task
/// scheduler, the companion's fixed install path and the protected setup folder, and holds no rule of its own.
/// </summary>
public static class AgentStartup
{
   private static readonly AgentStartupTasks Installed = new(new WindowsTaskScheduler(), InstalledExecutable, ProtectedStaging.MachineSetup());



   /// <summary>The only program the sign-in task may launch: the companion at its fixed machine-wide install path, so the task can never be pointed at another executable.</summary>
   private static string InstalledExecutable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "QQQ Print Agent", "App", "Qqq.PrintAgent.exe");



   /// <summary>Query only; never registers a task. Call off the UI thread.</summary>
   [ExcludeFromCodeCoverage(Justification = "Runs the startup rules against the real task scheduler of this computer, which a test must not query or change. It only passes the call on; the rules it runs are tested in AgentStartupTasks with a fake scheduler, and the staging it binds them to in ProtectedStaging.MachineSetup.")]
   public static AgentStartupStatus GetStatus() => Installed.GetStatusAsync().GetAwaiter().GetResult();



   /// <summary>
   /// Only an explicit user action calls this method; enabling authorizes an elevated task for the current administrator.
   /// Disabling deletes only a task that is exactly ours, and enabling is allowed only from the installed companion.
   /// </summary>
   [ExcludeFromCodeCoverage(Justification = "Runs the startup rules against the real task scheduler of this computer, which a test must not query or change. It only passes the call on; the rules it runs are tested in AgentStartupTasks with a fake scheduler, and the staging it binds them to in ProtectedStaging.MachineSetup.")]
   public static Task<AgentStartupStatus> SetEnabledAsync(bool enabled) => Installed.SetEnabledAsync(enabled);



   /// <summary>Full-uninstall cleanup removes only task definitions that exactly match this application's fixed identity.</summary>
   [ExcludeFromCodeCoverage(Justification = "Runs the startup rules against the real task scheduler of this computer, which a test must not query or change. It only passes the call on; the rules it runs are tested in AgentStartupTasks with a fake scheduler, and the staging it binds them to in ProtectedStaging.MachineSetup.")]
   public static Task RemoveInstalledTasksAsync() => Installed.RemoveInstalledTasksAsync();



   /// <summary>Whether a registered task is exactly the one this application creates for the installed companion; see <see cref="AgentStartupTasks.IsOwnedTaskDefinition"/>.</summary>
   internal static bool IsOwnedTaskDefinition(string taskName, string xml) => Installed.IsOwnedTaskDefinition(taskName, xml);



   /// <summary>Whether a path is the real installed companion; see <see cref="AgentStartupTasks.IsInstalledCompanion"/>.</summary>
   internal static bool IsInstalledCompanion(string? executable) => Installed.IsInstalledCompanion(executable);



   /// <summary>The task definition for one user and the installed companion; see <see cref="AgentStartupTasks.CreateTaskXml"/>.</summary>
   internal static string CreateTaskXml(string userSid) => Installed.CreateTaskXml(userSid);
}
