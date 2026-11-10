/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// The service's health word turned into a color level, the sentence shown beside it and the name of the tray icon, so
/// color is never the only signal. Any word this build does not know is treated as red.
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
}
