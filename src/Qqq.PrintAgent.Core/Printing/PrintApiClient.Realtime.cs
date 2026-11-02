/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

public sealed partial class PrintApiClient
{
   private PrintingRealtimeChannel? _realtime;
   private Func<PrintingRealtimeChannel>? _realtimeProvider;
   private PrintingRealtimeChannel RealtimeChannel => _realtimeProvider?.Invoke() ?? _realtime ?? throw new IOException("Realtime connection unavailable.");

   public PrintApiClient WithRealtime(PrintingRealtimeChannel channel, RealtimeSettings settings, Func<PrintingRealtimeChannel>? provider = null)
   {
      _realtime = channel;
      _realtimeProvider = provider;
      _http.Timeout = TimeSpan.FromMilliseconds(settings.RequestTimeoutMillis);
      return this;
   }

   public bool UsesRealtime => _realtime is not null;
}
