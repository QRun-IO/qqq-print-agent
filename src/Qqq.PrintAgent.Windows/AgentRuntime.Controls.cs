/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Net;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Remote-control part of the runtime. It holds the server's desired state (connected or disconnected, paused or not)
/// as the realtime channel delivers it, decides from it whether ordinary traffic and dispatch may happen, and delivers the
/// receipts of executed commands. A restriction is never relaxed by a failure to synchronize, so an unreachable server
/// cannot re-enable printing.
/// </summary>
public sealed partial class AgentRuntime
{
   private readonly SemaphoreSlim _controlExecutionGate = new(1, 1);
   private readonly Lock _controlPersistence = new();
   private AgentControlState _controlState = AgentControlState.Initial;
   private bool _controlReady;



   /// <summary>
   /// Called by the command path just before it saves a command's result, while it holds the control-state persistence lock.
   /// The service leaves it unset. A test sets it to deliver a server frame at exactly that moment, which no socket or store
   /// double can do: every write the command makes before this one takes the same journal lock.
   /// </summary>
   internal Action? ControlResultSaving { get; init; }



   /// <summary>
   /// True when the service may exchange ordinary traffic with QQQ: authorized and connected over the realtime channel,
   /// control state synchronized, and the server wants the device CONNECTED. Evaluated under the state lock so a concurrent
   /// restriction is never missed.
   /// </summary>
   private bool NormalTrafficAllowed
   {
      get
      {
         lock(_stateLock)
         {
            return _authorizationReady && _realtimeReady && _controlReady && string.Equals(_controlState.DesiredState.ConnectionState, "CONNECTED", StringComparison.Ordinal);
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
            return NormalTrafficAllowed && !_controlState.DesiredState.DispatchPaused && _inventoryReady;
         }
      }
   }



   /// <summary>
   /// Saves the control state now in force and returns what was saved. Two workers save it, the one that applies the
   /// server's state frames and the one that records command results, and the reader changes it in memory at any moment.
   /// Both savers take the same lock and save the value they read inside it, so the journal always ends up holding the
   /// newest state and never an older one written late.
   /// </summary>
   private AgentControlState PersistControlState(AgentStore store)
   {
      lock(_controlPersistence)
      {
         AgentControlState current;
         lock(_stateLock)
         { current = _controlState; }
         store.SaveControlState(current);
         return current;
      }
   }



   /// <summary>
   /// Records the last executed command beside the desired state. Only the three last-command fields are changed, in the
   /// state as it is at that moment. A copy taken earlier must never be written back whole: a desired state the reader
   /// applied in between would be replaced by the older one, in memory and on disk. Readiness and the claim loop are not
   /// touched, because a command result changes neither.
   /// </summary>
   private void RecordCommandResult(AgentStore store, string action, string outcome, string completedAt)
   {
      lock(_controlPersistence)
      {
         AgentControlState merged;
         lock(_stateLock)
         {
            merged = _controlState with { LastCommand = action, LastOutcome = outcome, LastCommandAt = completedAt };
            _controlState = merged;
         }
         ControlResultSaving?.Invoke();
         store.SaveControlState(merged);
      }
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
