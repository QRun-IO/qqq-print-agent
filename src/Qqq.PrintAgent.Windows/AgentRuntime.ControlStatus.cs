/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Connection transition and status wording part of the runtime. Enrollment and sign-in must not race printing, so they run
/// as a transition that cancels the live connection and waits for in-flight work; the status text turns the server's remote
/// controls (disconnect, pause, authorization demand) into the plain words shown in the companion.
/// </summary>
public sealed partial class AgentRuntime
{
   /// <summary>Runs enrollment as a connection transition, so it cannot overlap a claim or a printing job.</summary>
   internal async Task<string> EnrollWithGatesAsync(PipeRequest request, CancellationToken token)
      => await ConnectionTransitionAsync(() => EnrollAsync(request, token), token).ConfigureAwait(false);



   /// <summary>
   /// Runs an action that changes the connection identity (enroll, sign in, sign out) while nothing else is using the
   /// connection. It takes the connection gate, bumps the generation so late callbacks from the old connection are ignored,
   /// cancels the realtime socket and the claim wave, waits for the wave to end, and holds the control gate during the action.
   /// Transition state and signalling are always restored afterwards.
   /// </summary>
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



   /// <summary>
   /// Handles the operator's reconnect request. When the server demands authorization or the administrator disconnected the
   /// device it explains why and does nothing; when not enrolled it says so; otherwise it wakes the claim loop, closes the live
   /// socket so a fresh one is made, and requests an immediate reconnect.
   /// </summary>
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



   /// <summary>
   /// Overrides the health color and text when the server's controls restrict the device: authorization required is red;
   /// disconnected or paused is orange, or red when a job needs review; a control synchronization failure is orange. Otherwise
   /// the normal status gets a short note about remote-control support.
   /// </summary>
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



   /// <summary>The sentence appended to the status about remote controls: unavailable on an older server, still being checked, or available with the last remote action and its outcome.</summary>
   private string ControlSuffix() => _controlState.Support switch
   {
      "LEGACY" => " Remote controls are unavailable on this server.",
      "UNKNOWN" => " Checking remote control support.",
      _ => _controlState.LastCommand is null ? " Remote controls available." : $" Last remote action: {AgentControlLabels.Action(_controlState.LastCommand)} — {AgentControlLabels.Outcome(_controlState.LastOutcome)}.",
   };
}
