/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// What the tray icon shows: the name of its icon (the worst health), its tooltip, the status line at the top of
/// its menu and one menu line for every server.
/// <para>
/// Windows shows at most <see cref="MaximumToolTipCharacters"/> UTF-16 units of a notification icon's tooltip, which
/// eight names of up to 64 units cannot fit. So with several servers the tooltip says the count in each state,
/// the worst state first (which is the health the icon shows), then names as many servers as fit, each with its
/// health in words, and says how many were left out ("+3 more"); the menu lists them all, each with its sentence. A server is in the tooltip whole or not
/// at all, so the text is never cut inside a name, never inside a surrogate pair, and never ends in a separator.
/// With one server, or none, the tooltip is the footer's sentence and the product's name, as in 1.x.
/// </para>
/// </summary>
public sealed record TrayStatus(string IconName, string ToolTip, string StatusLine, IReadOnlyList<string> ServerLines)
{
   /// <summary>The most UTF-16 units of a notification icon's tooltip that Windows shows.</summary>
   public const int MaximumToolTipCharacters = 127;

   /// <summary>What stands between the parts of a tooltip that lists servers.</summary>
   private const string Separator = " · ";

   /// <summary>What stands between a sentence and the product's name, as in 1.x.</summary>
   private const string Dash = " — ";

   /// <summary>What a sentence that had to be cut ends with.</summary>
   private const string Ellipsis = "…";



   /// <summary>Builds the tray's texts from what is known of the service, the footer and the servers' states, in the snapshot's order.</summary>
   public static TrayStatus From(ServiceAvailability availability, FooterStatus footer, IReadOnlyList<ServerStatus> servers, string productName)
   {
      ArgumentNullException.ThrowIfNull(availability);
      ArgumentNullException.ThrowIfNull(footer);
      ArgumentNullException.ThrowIfNull(servers);
      ArgumentNullException.ThrowIfNull(productName);
      string toolTip = availability.StoredDataReady && servers.Count > 1
         ? Listing(footer.Text, [.. servers.Select(server => ShellMessages.TrayLine(server.Name, server.Health.Text))])
         : Sentence(footer.Text, productName);
      return new TrayStatus(HealthStatus.TrayIconName(footer.Health.Level), toolTip, footer.Text, [.. servers.Select(server => ShellMessages.TrayLine(server.Name, server.Sentence))]);
   }



   /// <summary>
   /// A tooltip of one sentence: the sentence and the product's name when both fit, the sentence alone when they do
   /// not, and the beginning of the sentence with an ellipsis when even it does not fit. The cut never splits a
   /// surrogate pair and leaves no space before the ellipsis.
   /// </summary>
   internal static string Sentence(string sentence, string productName)
   {
      string full = sentence + Dash + productName;
      return full.Length <= MaximumToolTipCharacters ? full
         : sentence.Length <= MaximumToolTipCharacters ? sentence
         : ManagementText.Bounded(sentence, MaximumToolTipCharacters - Ellipsis.Length).TrimEnd() + Ellipsis;
   }



   /// <summary>
   /// A tooltip that lists servers after a head: the head, then as many of the servers' entries as fit, in order,
   /// then how many were left out when any were. The largest number of entries is taken for which the whole text,
   /// with its note of what was left out, is within the limit. A head that does not fit by itself is cut as a
   /// sentence is.
   /// </summary>
   internal static string Listing(string head, IReadOnlyList<string> entries)
   {
      for(int shown = entries.Count; shown >= 0; shown--)
      {
         string text = string.Join(Separator, [head, .. entries.Take(shown), .. shown < entries.Count ? [ShellMessages.More(entries.Count - shown)] : Array.Empty<string>()]);
         if(text.Length <= MaximumToolTipCharacters)
         {
            return text;
         }
      }
      return ManagementText.Bounded(head, MaximumToolTipCharacters - Ellipsis.Length).TrimEnd() + Ellipsis;
   }
}
