/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>The window's four pages: which one a navigation choice means and the sentence shown under its title.</summary>
public static class ShellPages
{
   /// <summary>The Printers page, which is also where an unknown choice lands.</summary>
   public const string Printers = "Printers";

   /// <summary>The Jobs page.</summary>
   public const string Jobs = "Jobs";

   /// <summary>The Logs page, which opens the shared log viewer.</summary>
   public const string Logs = "Logs";

   /// <summary>The Settings page, reached through the navigation's built-in settings item.</summary>
   public const string Settings = "Settings";



   /// <summary>Names the page for a navigation choice: Settings for the built-in settings item, otherwise the chosen item's tag, and Printers when there is none.</summary>
   public static string Name(bool settingsSelected, string? tag) => settingsSelected ? Settings : tag ?? Printers;



   /// <summary>The one-sentence description shown under a page's title.</summary>
   public static string Description(string page) => page switch
   {
      Jobs => "Recent jobs received by this computer.",
      Logs => "Activity and diagnostics in the AppKit log viewer.",
      Settings => "Connection, appearance and application information.",
      _ => "Choose which printers this computer shares with the server.",
   };
}
