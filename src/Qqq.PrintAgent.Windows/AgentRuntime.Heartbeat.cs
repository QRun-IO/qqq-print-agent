/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Connection generation and legacy heartbeat part of the runtime. The generation number marks which connection a callback belongs to, so
/// a late result from a replaced connection can be recognised and ignored instead of overwriting newer state.
/// </summary>
public sealed partial class AgentRuntime
{
   private long _connectionGeneration;
   private string? _heartbeatFailure;
   internal TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);



   /// <summary>Invalidate in-flight responses from the prior enrollment without clearing durable job evidence.</summary>
   private void ResetConnectionState()
   {
      using var store = new AgentStore(directory, recoverUnreadableConfiguration: true);
      RealtimeSettings settings = store.LoadRealtimeSettings();
      lock(_stateLock)
      {
         _realtimeLifetime?.Cancel();
         _realtimeReady = false;
         if(_reconnectRequested.CurrentCount == 0)
         { _reconnectRequested.Release(); }
         _connectionGeneration++;
         _lastSuccessfulPoll = null;
         _failure = null;
         _heartbeatFailure = null;
         _controlState = AgentControlState.Initial;
         _realtimeSettings = settings;
         _controlReady = false;
         _controlFailure = null;
      }
      SignalPipeline();
   }



   /// <summary>Cloud liveness continues independently while rendering or spooler submission is active.</summary>
   private async Task HeartbeatAsync(CancellationToken token)
   {
      while(!token.IsCancellationRequested)
      {
         long generation;
         lock(_stateLock)
         {
            generation = _connectionGeneration;
         }
         try
         {
            using AgentStore store = new(directory);
            AgentConfiguration? configuration = store.LoadConfiguration();
            if(configuration is not null && NormalTrafficAllowed)
            {
               using PrintApiClient api = CreateApi(configuration);
               await api.HeartbeatAsync(store.GetDeviceProfile(), token).ConfigureAwait(false);
               lock(_stateLock)
               {
                  if(generation == _connectionGeneration)
                  {
                     _lastSuccessfulPoll = DateTimeOffset.UtcNow;
                     _heartbeatFailure = null;
                  }
               }
            }
         }
         catch(Exception exception) when(exception is not OperationCanceledException || !token.IsCancellationRequested)
         {
            bool changed = false;
            lock(_stateLock)
            {
               if(generation == _connectionGeneration)
               {
                  changed = !string.Equals(_heartbeatFailure, exception.Message, StringComparison.Ordinal);
                  _heartbeatFailure = exception.Message;
               }
            }
            if(changed)
            {
               WriteLog("ERROR", "Heartbeat failed: " + exception.Message);
            }
         }
         await Task.Delay(HeartbeatInterval, token).ConfigureAwait(false);
      }
   }
}
