/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using System.Threading.Channels;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Realtime housekeeping part of the runtime: liveness pings, inventory publishing, delivery of receipts that were
/// recorded locally, execution of server commands, and the bounds on queued inbound frames. None of it polls for jobs.
/// </summary>
public sealed partial class AgentRuntime
{
   /// <summary>
   /// Keeps the connection visibly alive while it is ready: a transport ping every interval, a heartbeat with the computer
   /// profile only when ordinary traffic is allowed (a paused or disconnected device pings but does not claim to be working),
   /// session renewal when it is due, and a status refresh request so the footer stays current.
   /// </summary>
   private async Task RealtimeHeartbeatAsync(PrintingRealtimeChannel channel, CancellationToken token)
   {
      while(!token.IsCancellationRequested)
      {
         if(_realtimeReady)
         {
            using var store = new AgentStore(directory);
            await channel.SendAsync("PING", new { }, token: token).ConfigureAwait(false);
            if(NormalTrafficAllowed)
            {
               await channel.SendAsync("HEARTBEAT", new { agentVersion = PrintApiClient.AgentVersion, profile = store.GetDeviceProfile() }, token: token).ConfigureAwait(false);
            }
            if(DateTimeOffset.UtcNow >= _nextSessionResume)
            { await EnsureAuthorizationAsync(token).ConfigureAwait(false); }
            RequestStatusSynchronization();
         }
         await Task.Delay(TimeSpan.FromSeconds(_realtimeSettings.HeartbeatIntervalSeconds), token).ConfigureAwait(false);
      }
   }



   /// <summary>
   /// Publishes the exposed printers whenever they change, and delivers pending receipts. It waits for a status request
   /// rather than a timer, sends inventory only when the serialized list differs from the last one sent, re-checks that traffic
   /// is still allowed before sending, and marks inventory ready (which lets claims start) only after the server accepted it.
   /// </summary>
   private async Task RealtimeSynchronizeAsync(CancellationToken token)
   {
      string? previousInventory = null;
      while(!token.IsCancellationRequested)
      {
         await _statusRequested.WaitAsync(token).ConfigureAwait(false);
         if(!_realtimeReady)
         { continue; }
         using var store = new AgentStore(directory);
         AgentConfiguration? configuration = store.LoadConfiguration();
         if(configuration is null)
         { continue; }
         using PrintApiClient api = CreateApi(configuration);
         if(NormalTrafficAllowed)
         {
            ExposedPrinterBackend backend = new(new ProfiledPrinterBackend(_printers, store), store.GetExposedPrinterIds);
            IReadOnlyList<PrinterInfo> inventory = backend.Discover();
            string serialized = JsonSerializer.Serialize(inventory.OrderBy(item => item.PrinterId, StringComparer.Ordinal), PrintApiClient.JsonOptions);
            if(!string.Equals(serialized, previousInventory, StringComparison.Ordinal))
            {
               if(!NormalTrafficAllowed)
               { continue; }
               await api.InventoryAsync(inventory, token).ConfigureAwait(false);
               previousInventory = serialized;
               lock(_stateLock)
               { _inventoryReady = true; }
               SignalPipeline();
               WakePoll();
            }
         }
         await ReconcileRealtimeReceiptsAsync(store, token).ConfigureAwait(false);
         _ = store.PruneHistory();
      }
   }



   /// <summary>
   /// Redelivers receipts that were recorded but not yet acknowledged: job events first, then control receipts, each under its
   /// own gate so the same receipt is never sent twice at once. An event is marked delivered only after the server accepted it.
   /// </summary>
   private async Task ReconcileRealtimeReceiptsAsync(AgentStore store, CancellationToken token)
   {
      AgentConfiguration? configuration = store.LoadConfiguration();
      if(configuration is null)
      { return; }
      using PrintApiClient api = CreateApi(configuration);
      await _eventDeliveryGate.WaitAsync(token).ConfigureAwait(false);
      try
      {
         foreach((string id, PrintEvent receipt) in store.Pending())
         {
            await api.EventAsync(id, receipt, token).ConfigureAwait(false);
            store.Acknowledge(receipt.EventId);
         }
      }
      finally { _eventDeliveryGate.Release(); }
      await _controlExecutionGate.WaitAsync(token).ConfigureAwait(false);
      try
      { await FlushControlReceiptsAsync(store, api, token).ConfigureAwait(false); }
      finally { _controlExecutionGate.Release(); }
   }



   /// <summary>
   /// Runs server commands one at a time in arrival order. Each waits until the connection is ready, executes under the control
   /// gate with the server's clock offset applied to its expiry, and has its receipt delivered straight afterwards. The command's
   /// inbound-queue reservation is released as soon as it is taken off the queue.
   /// </summary>
   private async Task RealtimeCommandsAsync(ChannelReader<RealtimeEnvelope> commands, CancellationToken token)
   {
      await foreach(RealtimeEnvelope envelope in commands.ReadAllAsync(token).ConfigureAwait(false))
      {
         ReleaseInbound(envelope);
         DeviceControlCommand command = envelope.Payload.Deserialize<DeviceControlCommand>(PrintApiClient.JsonOptions) ?? throw new InvalidDataException("Missing command.");
         while(true)
         {
            Task changed;
            lock(_stateLock)
            {
               if(_realtimeReady)
               { break; }
               changed = _pipelineChanged.Task;
            }
            await changed.WaitAsync(token).ConfigureAwait(false);
         }
         await _controlExecutionGate.WaitAsync(token).ConfigureAwait(false);
         try
         {
            using var store = new AgentStore(directory);
            AgentConfiguration? configuration = store.LoadConfiguration();
            if(configuration is null)
            { throw new InvalidOperationException("Device configuration is unavailable."); }
            using PrintApiClient api = CreateApi(configuration);
            await ApplyControlCommandAsync(store, api, command, DateTimeOffset.UtcNow + _serverTimeOffset, token).ConfigureAwait(false);
            await FlushControlReceiptsAsync(store, api, token).ConfigureAwait(false);
         }
         finally { _controlExecutionGate.Release(); }
      }
   }



   /// <summary>Asks the synchronizer to run once. The single-slot semaphore coalesces repeated requests, so a burst of events causes one pass.</summary>
   private void RequestStatusSynchronization()
   {
      lock(_stateLock)
      { if(_statusRequested.CurrentCount == 0) { _statusRequested.Release(); } }
   }



   /// <summary>Asks the queued-job preview to refresh once, coalescing repeated requests the same way.</summary>
   private void RequestQueueRefresh()
   {
      lock(_stateLock)
      { if(_queueRefreshRequested.CurrentCount == 0) { _queueRefreshRequested.Release(); } }
   }



   /// <summary>Counts an inbound frame against the queued-bytes and queued-frames limits from the server's settings and throws when either is exceeded, which closes the connection instead of letting memory grow without bound.</summary>
   private void ReserveInbound(RealtimeEnvelope envelope)
   {
      if(Interlocked.Add(ref _inboundBytes, envelope.WireLength) > _realtimeSettings.MaxQueuedFrameBytes
         || Interlocked.Increment(ref _inboundFrames) > _realtimeSettings.MaxQueuedFrames)
      {
         throw new InvalidDataException("Realtime incoming queue exceeded its configured bound.");
      }
   }



   /// <summary>Returns a frame's bytes and slot to the inbound limits once it has been taken off its queue.</summary>
   private void ReleaseInbound(RealtimeEnvelope envelope)
   {
      Interlocked.Add(ref _inboundBytes, -envelope.WireLength);
      Interlocked.Decrement(ref _inboundFrames);
   }
}
