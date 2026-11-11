/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Installer-only branding of an owned all-users shortcut; application identity never changes. This class is the entry
/// point for the installed product: it applies the rules in <see cref="AgentBrandingShortcuts"/> to the real shell, folder
/// security, registry and install paths, and holds no rule of its own.
/// </summary>
public static class AgentBrandingInstaller
{
   /// <summary>The places of the installed product, with the machine activity log as the place notes go.</summary>
   private static readonly BrandingPlaces Places = BrandingPlaces.Installed(WarnInMachineLog);



   /// <summary>The rules bound to the real shell, folder security, the product's registry key and the installed places; the record's warnings go where the notes go.</summary>
   private static readonly AgentBrandingShortcuts Installed = new(new WindowsShortcutShell(), new WindowsPathSecurity(), RegistryShortcutRecord.Machine(Places.TryNote), Places);



   /// <summary>Writes one warning to the machine activity log; branding uses it for the notes of a run that left something alone.</summary>
   [ExcludeFromCodeCoverage(Justification = "Opens the machine activity log under ProgramData and writes one warning entry to it. A test must not write that log: it belongs to the installed product and is protected from ordinary users. It passes the text on unchanged and decides nothing but that a note is a warning. When a note is written and what it says is decided in AgentBrandingShortcuts and RegistryShortcutRecord, its installer mark is added in BrandingPlaces.Installed, and a log that cannot be written is handled in BrandingPlaces.TryNote; all are tested with a list in place of the log.")]
   private static void WarnInMachineLog(string text) => AgentActivityLog.Create(AgentStore.DefaultDirectory).Warning(text);



   /// <summary>
   /// Creates or renames the all-users Start menu shortcut to the customer's brand name; see
   /// <see cref="AgentBrandingShortcuts.ApplyInstalledBranding"/> for what it requires and refuses.
   /// </summary>
   [ExcludeFromCodeCoverage(Justification = "Runs the branding rules against this computer's all-users Start menu, registry and install folder, which a test must not change and which need administrator rights. It only passes the call on; the rules it runs are tested in AgentBrandingShortcuts with fakes, and the places it binds them to in BrandingPlaces.Installed.")]
   public static void ApplyInstalledBranding() => Installed.ApplyInstalledBranding();



   /// <summary>Removes the shortcuts recorded by earlier branding runs, and only those that are still this installer's; see <see cref="AgentBrandingShortcuts.RemoveInstalledShortcut"/>.</summary>
   [ExcludeFromCodeCoverage(Justification = "Runs the branding rules against this computer's all-users Start menu, registry and install folder, which a test must not change and which need administrator rights. It only passes the call on; the rules it runs are tested in AgentBrandingShortcuts with fakes, and the places it binds them to in BrandingPlaces.Installed.")]
   public static void RemoveInstalledShortcut() => Installed.RemoveInstalledShortcut();



   /// <summary>Whether a shortcut that says this belongs to this application as installed; see <see cref="AgentBrandingShortcuts.MatchesOwnedShortcut"/>.</summary>
   internal static bool MatchesOwnedShortcut(string target, string arguments, string description) => Installed.MatchesOwnedShortcut(target, arguments, description);
}
