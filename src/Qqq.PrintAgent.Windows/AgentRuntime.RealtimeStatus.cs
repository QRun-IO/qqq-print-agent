using System.Threading.Channels;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

public sealed partial class AgentRuntime
{
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

   private void RequestStatusSynchronization()
   {
      lock(_stateLock)
      { if(_statusRequested.CurrentCount == 0) { _statusRequested.Release(); } }
   }

   private void RequestQueueRefresh()
   {
      lock(_stateLock)
      { if(_queueRefreshRequested.CurrentCount == 0) { _queueRefreshRequested.Release(); } }
   }

   private void ReserveInbound(RealtimeEnvelope envelope)
   {
      if(Interlocked.Add(ref _inboundBytes, envelope.WireLength) > _realtimeSettings.MaxQueuedFrameBytes
         || Interlocked.Increment(ref _inboundFrames) > _realtimeSettings.MaxQueuedFrames)
      {
         throw new InvalidDataException("Realtime incoming queue exceeded its configured bound.");
      }
   }

   private void ReleaseInbound(RealtimeEnvelope envelope)
   {
      Interlocked.Add(ref _inboundBytes, -envelope.WireLength);
      Interlocked.Decrement(ref _inboundFrames);
   }
}
