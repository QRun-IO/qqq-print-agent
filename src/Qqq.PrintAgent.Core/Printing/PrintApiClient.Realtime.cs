/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// Realtime part of the API client: attaches the WebSocket channel that inventory, job events and command receipts travel over, each with
/// the server's acknowledgement. Nothing falls back to HTTP: a send made with no channel attached fails as a closed connection.
/// </summary>
public sealed partial class PrintApiClient
{
   private PrintingRealtimeChannel? _realtime;
   private Func<PrintingRealtimeChannel>? _realtimeProvider;



   /// <summary>The channel to send on: the provider's current one when a provider was given (so a reconnect is picked up), otherwise the attached one; with neither it throws that the channel is closed, the failure every other "the connection is gone" raises.</summary>
   private PrintingRealtimeChannel RealtimeChannel => _realtimeProvider?.Invoke() ?? _realtime ?? throw new RealtimeChannelClosedException("Realtime connection unavailable.", null);



   /// <summary>Attaches a realtime channel (and an optional provider for the current one) and applies the server's request timeout; it returns this client so setup can be chained.</summary>
   public PrintApiClient WithRealtime(PrintingRealtimeChannel channel, RealtimeSettings settings, Func<PrintingRealtimeChannel>? provider = null)
   {
      _realtime = channel;
      _realtimeProvider = provider;
      _http.Timeout = TimeSpan.FromMilliseconds(settings.RequestTimeoutMillis);
      return this;
   }
}
