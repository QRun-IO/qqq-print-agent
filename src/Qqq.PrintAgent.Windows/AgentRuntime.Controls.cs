/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Net;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Remote-control part of the runtime. It holds the server's desired state (connected, paused, authorization required)
/// as the realtime channel delivers it, decides from it whether ordinary traffic and dispatch may happen, and delivers the
/// receipts of executed commands. A restriction is never relaxed by a failure to synchronize, so an unreachable server
/// cannot re-enable printing.
/// </summary>
public sealed partial class AgentRuntime
{
   private readonly SemaphoreSlim _controlExecutionGate = new(1, 1);
   private AgentControlState _controlState = AgentControlState.Initial;
   private bool _controlReady;



   /// <summary>
   /// True when the service may exchange ordinary traffic with QQQ: authorized and connected over the realtime channel,
   /// control state synchronized, no authorization demand, and the server wants the device CONNECTED. Evaluated under the
   /// state lock so a concurrent restriction is never missed.
   /// </summary>
   private bool NormalTrafficAllowed
   {
      get
      {
         lock(_stateLock)
         {
            return _authorizationReady && _realtimeReady && _controlReady && !_controlState.AuthorizationRequired && string.Equals(_controlState.DesiredState.ConnectionState, "CONNECTED", StringComparison.Ordinal);
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
   /// Saves a new control state, then publishes it. The disk write comes first so a crash cannot leave a looser state on
   /// restart. The claim loop is woken when the desired state changed, and the pipeline is signalled so waiting lanes
   /// re-evaluate whether they may proceed.
   /// </summary>
   private void SetControlState(AgentStore store, AgentControlState value)
   {
      store.SaveControlState(value);
      bool wake;
      lock(_stateLock)
      {
         wake = value.DesiredState != _controlState.DesiredState;
         _controlState = value;
         _controlReady = true;
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
