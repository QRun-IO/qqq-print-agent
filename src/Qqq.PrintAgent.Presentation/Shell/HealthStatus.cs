/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// The service's health word turned into a color level, the sentence shown beside it and the name of the tray icon, so
/// color is never the only signal. There are two readings of a word this build does not know. <see cref="From"/> is
/// the rule of the 1.x window, which the window still uses: any such word is red. <see cref="LevelOf"/> is the rule
/// for a server of a version 2 snapshot: such a word is <see cref="HealthLevel.Unknown"/>, neither healthy nor a
/// failure.
/// </summary>
public sealed record HealthStatus(HealthLevel Level, string Label, string IconName)
{
   /// <summary>Reads the health word the service reports ("GREEN", "ORANGE" or anything else).</summary>
   public static HealthStatus From(string? health) => health switch
   {
      "GREEN" => new HealthStatus(HealthLevel.Green, "Connected and ready", "green"),
      "ORANGE" => new HealthStatus(HealthLevel.Orange, "Connected · printers need attention", "orange"),
      _ => new HealthStatus(HealthLevel.Red, "Disconnected or needs attention", "red"),
   };



   /// <summary>
   /// Builds the tray tooltip from a status and the product name, cut to the 127 UTF-16 characters Windows allows for a
   /// notification icon without splitting a surrogate pair, so the tooltip is never rejected or shown with a broken character.
   /// </summary>
   public static string TrayToolTip(string status, string productName)
   {
      string text = status + " — " + productName;
      if(text.Length <= 127)
      {
         return text;
      }
      int length = char.IsHighSurrogate(text[125]) ? 125 : 126;
      return text[..length] + "…";
   }



   /// <summary>
   /// Reads the health word of one server in a version 2 snapshot. A word this build does not know, or none, is
   /// <see cref="HealthLevel.Unknown"/>: the contract says a client treats a health it does not know as unknown, so it
   /// is neither taken for healthy nor reported as a failure. (<see cref="From"/> keeps the rule of the 1.x window,
   /// where any other word is red.)
   /// </summary>
   public static HealthLevel LevelOf(string? health) => health switch
   {
      "GREEN" => HealthLevel.Green,
      "ORANGE" => HealthLevel.Orange,
      "RED" => HealthLevel.Red,
      _ => HealthLevel.Unknown,
   };



   /// <summary>
   /// The worst of several levels, for the footer and the tray, which show one health for every server together. Red
   /// is worst, because something is known to need the operator. Unknown comes next: it is not known to be fine, so
   /// it outranks orange and green and can never be hidden behind them. No levels at all is green; a caller with no
   /// servers says what that means itself.
   /// </summary>
   public static HealthLevel Worst(IEnumerable<HealthLevel> levels)
   {
      ArgumentNullException.ThrowIfNull(levels);
      return levels.Aggregate(HealthLevel.Green, static (worst, level) => Rank(level) > Rank(worst) ? level : worst);
   }



   /// <summary>The name of the tray icon for a level: the three 1.x names, and "unknown" for a health that is not known.</summary>
   public static string TrayIconName(HealthLevel level) => level switch
   {
      HealthLevel.Green => "green",
      HealthLevel.Orange => "orange",
      HealthLevel.Red => "red",
      _ => "unknown",
   };



   /// <summary>How bad a level is, for <see cref="Worst"/> and for naming the worst servers first: green, orange, unknown, red.</summary>
   internal static int Rank(HealthLevel level) => level switch
   {
      HealthLevel.Green => 0,
      HealthLevel.Orange => 1,
      HealthLevel.Red => 3,
      _ => 2,
   };
}
