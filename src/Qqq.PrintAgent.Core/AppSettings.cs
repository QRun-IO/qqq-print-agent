/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using KofTwentyTwo.AppKit.Settings;


namespace Qqq.PrintAgent.Core;

/// <summary>
/// The app's preferences: AppKit's shell settings (theme, splash, update checks) plus the
/// app's own. Add properties here, repair them in <see cref="Sanitize"/>, and they are
/// saved by <see cref="AppIdentity.SettingsStore"/>.
/// </summary>
public sealed class AppSettings : ShellSettings
{
   /// <summary>The last QQQ server address used by setup.</summary>
   public string ServerUrl { get; set; } = "";



   /// <summary>Repairs the shell settings, then restores a missing server address (JSON can supply null).</summary>
   public override void Sanitize()
   {
      base.Sanitize();
      ServerUrl ??= "";
   }
}

