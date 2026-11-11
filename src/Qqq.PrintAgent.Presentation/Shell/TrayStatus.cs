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
/// health in words, and says how many were left out ("+3 more"). The servers are named worst first, so the ones
/// that need the operator are the ones named; the menu lists them all, in the tree's order, each with its sentence.
/// When no server fits, the tooltip is the counts alone, which say how many servers there are: "+8 more" after
/// counts of eight servers would read as eight further ones. A server is in the tooltip whole or not at all, so the
/// text is never cut inside a name, never inside a surrogate pair, and never ends in a separator. The product's
/// name ends the tooltip when there is room for it after everything else.
/// With one server, or none, the tooltip is the footer's sentence and the product's name. That is the form of the
/// 1.x tooltip; the 1.x tooltip carried the health's own sentence, not the footer's, so the words differ wherever
/// the footer says more than the health (ADR-0005 lists where).
/// </para>
/// <para>
/// Two of these are equal when they say the same, so the shell tells a view about a new one only when something it
/// shows has changed.
/// </para>
/// </summary>
public sealed record TrayStatus(string IconName, string ToolTip, string StatusLine, IReadOnlyList<string> ServerLines)
{
   /// <summary>The most UTF-16 units of a notification icon's tooltip that Windows shows.</summary>
   public const int MaximumToolTipCharacters = 127;

   /// <summary>What stands between the parts of a tooltip that lists servers.</summary>
   private const string Separator = " · ";

   /// <summary>What stands between what the tooltip says and the product's name, as in 1.x.</summary>
   private const string Dash = " — ";

   /// <summary>What a sentence that had to be cut ends with.</summary>
   private const string Ellipsis = "…";



   /// <summary>True when the other one shows the same icon and says the same in its tooltip, its status line and every server's line.</summary>
   public bool Equals(TrayStatus? other)
      => other is not null && string.Equals(IconName, other.IconName, StringComparison.Ordinal) && string.Equals(ToolTip, other.ToolTip, StringComparison.Ordinal)
         && string.Equals(StatusLine, other.StatusLine, StringComparison.Ordinal) && ServerLines.SequenceEqual(other.ServerLines, StringComparer.Ordinal);



   /// <summary>A hash that is the same for two that are equal.</summary>
   public override int GetHashCode() => HashCode.Combine(IconName, ToolTip, StatusLine, ServerLines.Count);



   /// <summary>Builds the tray's texts from what is known of the service, the footer and the servers' states, in the snapshot's order.</summary>
   public static TrayStatus From(ServiceAvailability availability, FooterStatus footer, IReadOnlyList<ServerStatus> servers, string productName)
   {
      ArgumentNullException.ThrowIfNull(availability);
      ArgumentNullException.ThrowIfNull(footer);
      ArgumentNullException.ThrowIfNull(servers);
      ArgumentNullException.ThrowIfNull(productName);
      string toolTip = availability.StoredDataReady && servers.Count > 1
         ? Listing(footer.Text, [.. servers.OrderByDescending(server => HealthStatus.Rank(server.Health.Level)).Select(server => ShellMessages.TrayLine(server.Name, server.Health.Text))], productName)
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
   /// A tooltip that lists servers after a head: the head, then as many of the servers' entries as fit, in the
   /// order given, then how many were left out when any were. The largest number of entries is taken for which the
   /// whole text, with its note of what was left out, is within the limit. When every entry fits and the product's
   /// name fits after them, it ends the text. When not even one entry fits, the text is the head as a tooltip of
   /// one sentence, with no note of what was left out: the head counts the servers, so it says how many there are,
   /// and one that does not fit by itself is cut as a sentence is.
   /// </summary>
   internal static string Listing(string head, IReadOnlyList<string> entries, string productName)
   {
      string all = string.Join(Separator, [head, .. entries]) + Dash + productName;
      if(all.Length <= MaximumToolTipCharacters)
      {
         return all;
      }
      for(int shown = entries.Count; shown > 0; shown--)
      {
         string text = string.Join(Separator, [head, .. entries.Take(shown), .. shown < entries.Count ? [ShellMessages.More(entries.Count - shown)] : Array.Empty<string>()]);
         if(text.Length <= MaximumToolTipCharacters)
         {
            return text;
         }
      }
      return Sentence(head, productName);
   }
}
