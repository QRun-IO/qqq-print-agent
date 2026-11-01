/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// Realtime part of the API client: attaches a WebSocket channel so inventory, heartbeats and events travel over it with the server's
/// acknowledgement instead of over separate HTTP calls.
/// </summary>
public sealed partial class PrintApiClient
{
   private PrintingRealtimeChannel? _realtime;
   private Func<PrintingRealtimeChannel>? _realtimeProvider;



   /// <summary>The channel to send on: the provider's current one when a provider was given (so a reconnect is picked up), otherwise the attached one; with neither it throws, never silently falling back to HTTP.</summary>
   private PrintingRealtimeChannel RealtimeChannel => _realtimeProvider?.Invoke() ?? _realtime ?? throw new IOException("Realtime connection unavailable.");



   /// <summary>Attaches a realtime channel (and an optional provider for the current one) and applies the server's request timeout; it returns this client so setup can be chained.</summary>
   public PrintApiClient WithRealtime(PrintingRealtimeChannel channel, RealtimeSettings settings, Func<PrintingRealtimeChannel>? provider = null)
   {
      _realtime = channel;
      _realtimeProvider = provider;
      _http.Timeout = TimeSpan.FromMilliseconds(settings.RequestTimeoutMillis);
      return this;
   }



   /// <summary>True when a realtime channel is attached, which tells the worker that heartbeats and events already flow over the socket.</summary>
   public bool UsesRealtime => _realtime is not null;
}
