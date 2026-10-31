using System.Diagnostics;
using System.Globalization;
using System.Net;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows;

public sealed partial class AgentRuntime
{
   private readonly SemaphoreSlim _controlExecutionGate = new(1, 1);
   private AgentControlState _controlState = AgentControlState.Initial;
   private bool _controlReady;
   private string? _controlFailure;
   internal TimeSpan? ControlIntervalOverride { get; init; }

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

   private static async Task FlushControlReceiptsAsync(AgentStore store, PrintApiClient api, CancellationToken token)
   {
      foreach((string commandId, DeviceControlReceipt receipt) in store.PendingControlReceipts())
      {
         await DeliverControlReceiptAsync(store, api, commandId, receipt, token).ConfigureAwait(false);
      }
   }

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

   private static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
}
