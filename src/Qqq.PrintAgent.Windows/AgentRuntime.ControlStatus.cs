/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

public sealed partial class AgentRuntime
{
   internal async Task<string> EnrollWithGatesAsync(PipeRequest request, CancellationToken token)
      => await ConnectionTransitionAsync(() => EnrollAsync(request, token), token).ConfigureAwait(false);

   private async Task<string> ConnectionTransitionAsync(Func<Task<string>> action, CancellationToken token)
   {
      await _connectionGate.WaitAsync(token).ConfigureAwait(false);
      try
      {
         Task active;
         lock(_stateLock)
         {
            _transitioning = true;
            _connectionGeneration++;
            _realtimeReady = false;
            _realtimeLifetime?.Cancel();
            _pipelineLifetime?.Cancel();
            active = _pipelineTask;
         }
         try
         { await active.WaitAsync(token).ConfigureAwait(false); }
         catch(OperationCanceledException) when(!token.IsCancellationRequested) { }
         await _controlExecutionGate.WaitAsync(token).ConfigureAwait(false);
         try
         { return await action().ConfigureAwait(false); }
         finally { _controlExecutionGate.Release(); }
      }
      finally
      {
         lock(_stateLock)
         { _transitioning = false; }
         SignalPipeline();
         _connectionGate.Release();
      }
   }

   private string ReconnectLocal()
   {
      lock(_stateLock)
      {
         if(_controlState.AuthorizationRequired)
         {
            return "Server authorization is required. Open Connection settings to restore authorization; job history is preserved.";
         }
         if(string.Equals(_controlState.DesiredState.ConnectionState, "DISCONNECTED", StringComparison.Ordinal))
         {
            return "Disconnected by the administrator. The realtime control channel remains active; ask the server administrator to reconnect this device.";
         }
      }
      using AgentStore store = new(directory);
      if(store.LoadConfiguration() is null)
      {
         return "Not enrolled. Open Connection settings to connect this device.";
      }
      WakePoll();
      lock(_stateLock)
      {
         _realtimeLifetime?.Cancel();
         if(_reconnectRequested.CurrentCount == 0)
         { _reconnectRequested.Release(); }
      }
      WriteLog("INFO", "Connection retry requested.");
      return "Reconnect requested. Connection status will update after the next server response.";
   }

   private (string Health, string Status) ControlStatus(string health, string status, bool jobFailure)
   {
      if(_controlState.AuthorizationRequired)
      {
         return ("RED", "Server authorization is required. Open Connection settings to restore authorization. Job history is preserved.");
      }
      if(string.Equals(_controlState.DesiredState.ConnectionState, "DISCONNECTED", StringComparison.Ordinal))
      {
         return (jobFailure ? "RED" : "ORANGE", "Disconnected by the administrator. The realtime control channel remains active for reconnect; in-flight print receipts are preserved." + ControlSuffix());
      }
      if(_controlState.DesiredState.DispatchPaused)
      {
         return (jobFailure ? "RED" : "ORANGE", "Dispatch paused by the administrator. No new jobs will be claimed; in-flight printing continues." + ControlSuffix());
      }
      if(_controlFailure is not null)
      {
         return ("ORANGE", status + " " + _controlFailure);
      }
      return (health, status + ControlSuffix());
   }

   private string ControlSuffix() => _controlState.Support switch
   {
      "LEGACY" => " Remote controls are unavailable on this server.",
      "UNKNOWN" => " Checking remote control support.",
      _ => _controlState.LastCommand is null ? " Remote controls available." : $" Last remote action: {AgentControlLabels.Action(_controlState.LastCommand)} — {AgentControlLabels.Outcome(_controlState.LastOutcome)}.",
   };
}
