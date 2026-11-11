/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Connection generation part of the runtime. The generation number marks which connection a callback belongs to, so a late
/// result from a replaced connection can be recognised and ignored instead of overwriting newer state.
/// </summary>
public sealed partial class AgentRuntime
{
   private long _connectionGeneration;
   private string? _heartbeatFailure;



   /// <summary>Invalidate in-flight responses from the prior enrollment without clearing durable job evidence.</summary>
   private void ResetConnectionState()
   {
      ResetPrinterCatalog();
      using AgentStore store = OpenStore(recover: true);
      RealtimeSettings settings = store.LoadRealtimeSettings();
      lock(_stateLock)
      {
         _realtimeLifetime?.Cancel();
         _realtimeReady = false;
         if(_reconnectRequested.CurrentCount == 0)
         { _reconnectRequested.Release(); }
         _connectionGeneration++;
         _lastServerContact = null;
         _heartbeatFailure = null;
         _controlState = AgentControlState.Initial;
         _realtimeSettings = settings;
         _controlReady = false;
      }
      _pipelineChanged.Raise();
   }
}
