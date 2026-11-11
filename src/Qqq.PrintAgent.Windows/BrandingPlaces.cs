/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Where branding works and what it reads: the installed companion, the all-users Start menu programs folder, the machine
/// state folder with the check that it is protected (it throws when the folder cannot be trusted), and the customer's
/// branding, loaded with or without the labels the installer recorded, and where a note goes when branding went ahead
/// although it found something it left alone. The installed product takes its places from <see cref="Installed"/>;
/// a test passes temporary folders, branding of its own and a list.
/// </summary>
internal sealed record BrandingPlaces(string InstalledExecutable, string StartMenuPrograms, string StateDirectory, Action<string> EnsureProtectedState, Func<bool, RuntimeBranding> LoadBranding, Action<string> Note)
{
   /// <summary>
   /// The places of the installed product, so the entry point cannot bind a weaker check by accident: the companion at
   /// its fixed path under Program Files, the all-users Start menu, the machine state folder checked by the machine state
   /// guard, and the machine's own branding. Only where notes are written is given, because the machine activity log
   /// cannot be opened in a test; each note is marked as coming from the installer here.
   /// </summary>
   internal static BrandingPlaces Installed(Action<string> log)
      => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "QQQ Print Agent", "App", "Qqq.PrintAgent.exe"),
         Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AgentStore.DefaultDirectory, MachineStateDirectory.EnsureForPath, RuntimeBranding.LoadMachine, note => log("[Installer] " + note));
}
