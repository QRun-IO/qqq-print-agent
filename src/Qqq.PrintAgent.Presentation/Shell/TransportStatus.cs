/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// The footer's connection state and traffic texts for one observation from the service: the WebSocket state, the
/// download and upload rates, and tooltips and help text with the exact totals and what they measure (service payload
/// bytes, not headers or TLS). With no observation every text says so plainly instead of leaving an old figure on screen.
/// </summary>
public sealed record TransportStatus(string StateText, string TrafficText, string StateToolTip, string TrafficToolTip, string TrafficHelpText)
{
   /// <summary>The texts shown when the service supplied no current observation.</summary>
   public static TransportStatus Unavailable { get; } = new("WebSocket status unavailable", "Traffic unavailable",
      "A current WebSocket observation is not available from the local service.",
      "The local service has not supplied a current traffic measurement.", "Traffic unavailable.");



   /// <summary>Builds the texts for an observation, or the unavailable texts when there is none.</summary>
   public static TransportStatus From(AgentTransportSnapshot? transport, TimestampFormat time)
   {
      ArgumentNullException.ThrowIfNull(time);
      if(transport is null)
      {
         return Unavailable;
      }
      string state = TransportFormatting.State(transport.Protocol, transport.State);
      string totals = $"Received {TransportFormatting.Bytes(transport.DownloadedBytes)} · Sent {TransportFormatting.Bytes(transport.UploadedBytes)} since service start ({time.Full(transport.TotalsSinceUtc)}).";
      return new TransportStatus(state,
         $"↓ {TransportFormatting.Rate(transport.DownloadBytesPerSecond)} · ↑ {TransportFormatting.Rate(transport.UploadBytesPerSecond)}",
         state + ". Dispatch permission and printer health are shown separately above.",
         totals + "\nQQQ service payload bytes, including documents and WebSocket messages; excludes headers, protocol framing and TLS. Rates cover the latest two seconds and refresh locally every second. Totals reset when the service restarts.",
         totals);
   }
}
