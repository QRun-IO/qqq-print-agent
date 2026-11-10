/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core;

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
      => await ConnectionTransitionAsync(() => EnrollNativeAsync(request, token), token).ConfigureAwait(false);



   /// <summary>
   /// Runs an action that changes the connection identity (enroll, sign in, sign out) while nothing else is using the
   /// connection. It takes the connection gate, bumps the generation so late callbacks from the old connection are ignored,
   /// cancels the realtime socket and the claim wave, waits for the wave to end, and holds the control gate during the action.
   /// Transition state and signalling are always restored afterwards, and the connection loop is asked to connect once
   /// more: a request it received during the action found the transition still in progress and made it back off.
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
         {
            _transitioning = false;
            if(_reconnectRequested.CurrentCount == 0)
            { _reconnectRequested.Release(); }
         }
         SignalPipeline();
         _connectionGate.Release();
      }
   }



   /// <summary>
   /// Handles the operator's reconnect request. A reconnect cannot repair a missing or withdrawn authorization, so when the
   /// service is blocked, or holds no usable sign-in, the reply says that a sign-in is required and nothing is retried. When
   /// the administrator disconnected the device it explains that; when not enrolled it says so. Otherwise it wakes the claim
   /// loop, closes the live socket so a fresh one is made, and requests an immediate reconnect.
   /// </summary>
   private string ReconnectLocal()
   {
      string signInRequired = $"Sign-in is required: {AppIdentity.ServerName} authorization is missing or was withdrawn, and reconnecting cannot restore it. Open Connection settings and sign in again; pairing and job history are preserved.";
      lock(_stateLock)
      {
         if(_authorizationBlocked)
         {
            return signInRequired;
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
      lock(_stateLock)
      {
         if(_transportNeedsSignIn)
         {
            return signInRequired;
         }
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
   /// Overrides the health color and text when the device is restricted: blocked authorization is red and says a sign-in is
   /// needed; disconnected or paused by the administrator is orange, or red when a job needs review. Otherwise the normal
   /// status gets a short note about remote-control support.
   /// </summary>
   private (string Health, string Status) ControlStatus(string health, string status, bool jobFailure)
   {
      if(_authorizationBlocked)
      {
         return ("RED", _authenticationMessage);
      }
      if(string.Equals(_controlState.DesiredState.ConnectionState, "DISCONNECTED", StringComparison.Ordinal))
      {
         return (jobFailure ? "RED" : "ORANGE", "Disconnected by the administrator. The realtime control channel remains active for reconnect; in-flight print receipts are preserved." + ControlSuffix());
      }
      if(_controlState.DesiredState.DispatchPaused)
      {
         return (jobFailure ? "RED" : "ORANGE", "Dispatch paused by the administrator. No new jobs will be claimed; in-flight printing continues." + ControlSuffix());
      }
      return (health, status + ControlSuffix());
   }



   /// <summary>
   /// The sentence appended to the status about remote controls: available, with the last remote action and its outcome, once
   /// the server has sent its state on the realtime channel; still being checked until then. Any other saved value, such as
   /// one written by the polling prerelease, reads as still being checked.
   /// </summary>
   private string ControlSuffix() => !string.Equals(_controlState.Support, "SUPPORTED", StringComparison.Ordinal) ? " Checking remote control support."
      : _controlState.LastCommand is null ? " Remote controls available."
      : $" Last remote action: {AgentControlLabels.Action(_controlState.LastCommand)} — {AgentControlLabels.Outcome(_controlState.LastOutcome)}.";
}
