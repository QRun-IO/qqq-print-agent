/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Globalization;
using System.Net;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Remote-control part of the runtime. It holds the server's desired state (connected, paused, authorization required)
/// and decides from it whether ordinary traffic and dispatch may happen. Restrictions are applied before they are saved
/// and are never relaxed by a failure to synchronize, so an unreachable server cannot re-enable printing.
/// </summary>
public sealed partial class AgentRuntime
{
   private readonly SemaphoreSlim _controlExecutionGate = new(1, 1);
   private AgentControlState _controlState = AgentControlState.Initial;
   private bool _controlReady;
   private string? _controlFailure;
   internal TimeSpan? ControlIntervalOverride { get; init; }



   /// <summary>
   /// True when the service may exchange ordinary traffic with QQQ: authorized and connected over the realtime channel
   /// (or using the injected test API), control state synchronized, no authorization demand, and the server wants the
   /// device CONNECTED. Evaluated under the state lock so a concurrent restriction is never missed.
   /// </summary>
   private bool NormalTrafficAllowed
   {
      get
      {
         lock(_stateLock)
         {
            return (apiFactory is not null || (_authorizationReady && _realtimeReady)) && _controlReady && !_controlState.AuthorizationRequired && string.Equals(_controlState.DesiredState.ConnectionState, "CONNECTED", StringComparison.Ordinal);
         }
      }
   }



   /// <summary>
   /// True when new work may be claimed and submitted: normal traffic is allowed, the administrator has not paused
   /// dispatch, and printer inventory has been delivered so the server knows what this device can print.
   /// </summary>
   private bool DispatchAllowed
   {
      get
      {
         lock(_stateLock)
         {
            return NormalTrafficAllowed && !_controlState.DesiredState.DispatchPaused && (apiFactory is not null || _inventoryReady);
         }
      }
   }



   /// <summary>
   /// One-shot compatibility loop that repeats a control poll at the interval the server returned. A failure keeps the
   /// existing restrictions and only records a readable message; the loop never loosens state on error. Production uses
   /// the persistent realtime channel instead of this loop.
   /// </summary>
   private async Task ControlLoopAsync(CancellationToken token)
   {
      while(!token.IsCancellationRequested)
      {
         int interval = 5;
         try
         {
            interval = await PollControlsOnceAsync(token).ConfigureAwait(false);
         }
         catch(Exception exception) when(exception is not OperationCanceledException || !token.IsCancellationRequested)
         {
            lock(_stateLock)
            {
               _controlFailure = "Remote control could not be synchronized. Existing restrictions remain in effect.";
            }
         }
         await Task.Delay(ControlIntervalOverride ?? TimeSpan.FromSeconds(interval), token).ConfigureAwait(false);
      }
   }



   /// <summary>
   /// Runs a single control poll and returns the seconds to wait before the next. It requires current authorization,
   /// reloads the saved control state, and abandons the pass when the connection generation changed meanwhile so a stale
   /// poll cannot overwrite newer state. A saved authorization demand slows polling instead of repeating requests.
   /// </summary>
   internal async Task<int> PollControlsOnceAsync(CancellationToken token)
   {
      if(!await EnsureAuthorizationAsync(token).ConfigureAwait(false))
      {
         return 5;
      }
      long generation;
      lock(_stateLock)
      {
         generation = _connectionGeneration;
      }
      using AgentStore store = new(directory);
      AgentConfiguration? configuration = store.LoadConfiguration();
      if(configuration is null)
      {
         return 5;
      }
      AgentControlState saved = store.LoadControlState();
      lock(_stateLock)
      {
         if(generation != _connectionGeneration)
         {
            return 5;
         }
         _controlState = saved;
      }
      if(saved.AuthorizationRequired)
      {
         return 30;
      }
      return await FetchControlPollAsync(store, configuration, saved, generation, token).ConfigureAwait(false);
   }



   /// <summary>
   /// Fetches and applies one poll under the control execution gate. A 401 or 403, or a session failure meaning sign-in or
   /// permission is gone, is recorded as an authorization demand and blocks traffic, but only if the generation is still
   /// current, so a late failure from an old connection cannot restrict the new one.
   /// </summary>
   private async Task<int> FetchControlPollAsync(AgentStore store, AgentConfiguration configuration, AgentControlState saved, long generation, CancellationToken token)
   {
      // This one-shot compatibility seam is exercised by existing protocol fixtures;
      // production RunAsync uses only the persistent native channel.
      using PrintApiClient api = apiFactory?.Invoke(configuration)
         ?? PrintApiClient.ForSession(_sessionClient ?? throw new InvalidOperationException("Sign in is required."));
      try
      {
         DeviceControlPoll? poll = await api.ControlPollAsync(saved.DesiredState.Revision, token).ConfigureAwait(false);
         await _controlExecutionGate.WaitAsync(token).ConfigureAwait(false);
         try
         {
            lock(_stateLock)
            {
               if(generation != _connectionGeneration)
               {
                  return 5;
               }
            }
            return await ApplyControlPollAsync(store, api, saved, poll, token).ConfigureAwait(false);
         }
         finally { _controlExecutionGate.Release(); }
      }
      catch(HttpRequestException exception) when(exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
      {
         lock(_stateLock)
         {
            if(generation == _connectionGeneration)
            {
               _controlState = saved with { AuthorizationRequired = true };
               store.SaveControlState(_controlState);
               _controlReady = false;
            }
         }
         return 30;
      }
      catch(QqqSessionException exception) when(exception.Failure is QqqSessionException.FailureKind.AuthenticationRequired or QqqSessionException.FailureKind.PermissionDenied)
      {
         lock(_stateLock)
         {
            if(generation == _connectionGeneration)
            {
               BlockAuthorization();
            }
         }
         return 30;
      }
   }



   /// <summary>
   /// Applies a poll response: the desired state first, then interrupted commands recovered as unknown, then each new
   /// command with receipts flushed before and after. A revision that goes backwards, or changes without a new revision,
   /// is rejected, and once the server has negotiated control a later absence of it is an error rather than a downgrade.
   /// </summary>
   private async Task<int> ApplyControlPollAsync(AgentStore store, PrintApiClient api, AgentControlState saved, DeviceControlPoll? poll, CancellationToken token)
   {
      if(poll is null)
      {
         if(string.Equals(saved.Support, "SUPPORTED", StringComparison.Ordinal))
         {
            throw new InvalidDataException("Negotiated device control is no longer available; restrictions were retained.");
         }
         SetControlState(store, saved with { Support = "LEGACY", LastContact = Now() });
         return 30;
      }
      if(poll.DesiredState.Revision < saved.DesiredState.Revision || (poll.DesiredState.Revision == saved.DesiredState.Revision
         && string.Equals(saved.Support, "SUPPORTED", StringComparison.Ordinal) && poll.DesiredState != saved.DesiredState))
      {
         throw new InvalidDataException("Device control revision regressed or changed without a new revision.");
      }
      SetControlState(store, saved with { Support = "SUPPORTED", DesiredState = poll.DesiredState, LastContact = Now() });
      long received = Stopwatch.GetTimestamp();
      store.RecoverInterruptedControlCommands();
      await FlushControlReceiptsAsync(store, api, token).ConfigureAwait(false);
      foreach(DeviceControlCommand command in poll.Commands)
      {
         await ApplyControlCommandAsync(store, api, command, DateTimeOffset.Parse(poll.ServerTime, CultureInfo.InvariantCulture) + Stopwatch.GetElapsedTime(received), token).ConfigureAwait(false);
      }
      await FlushControlReceiptsAsync(store, api, token).ConfigureAwait(false);
      return poll.PollIntervalSeconds;
   }



   /// <summary>
   /// Saves a new control state, then publishes it. The disk write comes first so a crash cannot leave a looser state on
   /// restart. The poll loop is woken when the desired state changed or control just became ready, and the pipeline is
   /// signalled so waiting lanes re-evaluate whether they may proceed.
   /// </summary>
   private void SetControlState(AgentStore store, AgentControlState value)
   {
      store.SaveControlState(value);
      bool wake;
      lock(_stateLock)
      {
         wake = (_pollStarted && !_controlReady) || value.DesiredState != _controlState.DesiredState;
         _controlState = value;
         _controlReady = true;
         _controlFailure = null;
      }
      if(wake)
      {
         WakePoll();
      }
      SignalPipeline();
   }



   /// <summary>Delivers every stored command receipt in order; a failure stops the flush so the rest stay stored and are retried.</summary>
   private static async Task FlushControlReceiptsAsync(AgentStore store, PrintApiClient api, CancellationToken token)
   {
      foreach((string commandId, DeviceControlReceipt receipt) in store.PendingControlReceipts())
      {
         await DeliverControlReceiptAsync(store, api, commandId, receipt, token).ConfigureAwait(false);
      }
   }



   /// <summary>
   /// Sends one receipt and marks it acknowledged only after the server accepted it. When the server says the command no
   /// longer exists (404 or 410) an already-expired receipt is retired instead of retried forever; any other case is rethrown.
   /// </summary>
   private static async Task DeliverControlReceiptAsync(AgentStore store, PrintApiClient api, string commandId, DeviceControlReceipt receipt, CancellationToken token)
   {
      try
      {
         await api.ControlReceiptAsync(commandId, receipt, token).ConfigureAwait(false);
         store.AcknowledgeControlReceipt(commandId);
      }
      catch(HttpRequestException exception) when(exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
      {
         if(!store.RetireExpiredControlReceipt(commandId))
         {
            throw;
         }
      }
   }



   /// <summary>Current UTC time in round-trip ISO 8601 form, the format stored in control state and receipts.</summary>
   private static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
}
