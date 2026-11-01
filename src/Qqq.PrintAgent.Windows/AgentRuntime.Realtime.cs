/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Realtime transport part of the runtime. It owns the WebSocket's lifetime: reconnect with jittered backoff, the
/// HELLO and WELCOME handshake, ordered application of server settings and desired state, and the wake-ups that start
/// claim drains. Nothing here polls the server for jobs or controls.
/// </summary>
public sealed partial class AgentRuntime
{
   private static readonly string[] RealtimeCapabilities = ["realtime-v1", "REFRESH_INVENTORY", "CANCEL_SPOOL"];
   private PrintingRealtimeChannel? _realtime;
   private RealtimeSettings _realtimeSettings = new();
   private bool _realtimeReady;
   private CancellationTokenSource? _realtimeLifetime;
   private readonly SemaphoreSlim _reconnectRequested = new(0, 1);
   private readonly SemaphoreSlim _eventDeliveryGate = new(1, 1);
   private int _reconnectAttempts;
   private TimeSpan _serverTimeOffset;
   private long _inboundBytes;
   private int _inboundFrames;
   private readonly SemaphoreSlim _statusRequested = new(0, 1);
   private readonly SemaphoreSlim _queueRefreshRequested = new(0, 1);
   private bool _inventoryReady;
   private long _stateSequence;



   /// <summary>
   /// A state frame tagged with an increasing sequence number, so that when several updates are applied in order only the
   /// newest one on the current connection can mark the runtime ready.
   /// </summary>
   private sealed record RealtimeStateUpdate(RealtimeEnvelope Envelope, long Sequence);



   /// <summary>
   /// Entry point of the realtime service loop. Before connecting it loads the persisted settings, control state and
   /// server origin, and records any job a previous run left incomplete as FAILED (submission never started) or
   /// OUTCOME_UNKNOWN (it was submitting), because only an operator can tell whether paper came out. It then runs the
   /// reconnect loop, claim drain, management pipe and queued-job refresh together; whichever ends first stops the
   /// others and the transport is marked stopped.
   /// </summary>
   private async Task RunRealtimeAsync(CancellationToken token)
   {
      using(var store = new AgentStore(directory, recoverUnreadableConfiguration: true))
      {
         _realtimeSettings = store.LoadRealtimeSettings();
         _controlState = store.LoadControlState();
         if(Uri.TryCreate(store.LoadConfiguration()?.ServerUrl, UriKind.Absolute, out Uri? origin))
         { _transportOrigin = origin; }
         foreach((string id, string previous) in store.Incomplete())
         {
            store.Record(id, new PrintEvent(Guid.NewGuid().ToString("N"), string.Equals(previous, "SUBMITTING", StringComparison.Ordinal) ? "OUTCOME_UNKNOWN" : "FAILED",
               string.Equals(previous, "SUBMITTING", StringComparison.Ordinal) ? "Agent stopped during native submission; inspect the printer before retrying." : "Agent stopped before native submission."));
         }
         store.RecoverInterruptedControlCommands();
      }
      using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
      Task connection = RealtimeReconnectAsync(lifetime.Token);
      Task jobs = RealtimeDrainAsync(lifetime.Token);
      Task pipe = ServePipeAsync(lifetime.Token);
      Task queue = RefreshQueuedOnSignalAsync(lifetime.Token);
      try
      { await Task.WhenAny(connection, jobs, pipe, queue).ConfigureAwait(false); }
      finally
      {
         lock(_stateLock)
         { _transportStopped = true; }
         await lifetime.CancelAsync().ConfigureAwait(false);
         await Task.WhenAll(connection, jobs, pipe, queue).ConfigureAwait(false);
      }
   }



   /// <summary>
   /// Keeps one authenticated socket alive for the current connection generation. Each failure marks realtime and
   /// control not ready and records a reason the operator can read; when the server says sign-in or permission is gone
   /// it blocks further attempts, because reconnecting cannot repair that. A generation change (sign-out, re-pairing)
   /// abandons the attempt without touching newer state. Between attempts it waits the backoff delay, cut short by an
   /// explicit reconnect request.
   /// </summary>
   private async Task RealtimeReconnectAsync(CancellationToken token)
   {
      while(!token.IsCancellationRequested)
      {
         long generation;
         lock(_stateLock)
         { generation = _connectionGeneration; }
         try
         {
            if(await EnsureAuthorizationAsync(token).ConfigureAwait(false))
            {
               await RealtimeConnectionAsync(generation, token).ConfigureAwait(false);
            }
         }
         catch(Exception exception) when(exception is not OperationCanceledException || !token.IsCancellationRequested)
         {
            lock(_stateLock)
            {
               if(generation != _connectionGeneration)
               { continue; }
               _realtimeReady = false;
               _controlReady = false;
               _heartbeatFailure = "Realtime connection unavailable. Reconnecting; local receipts are retained.";
               if(exception is QqqSessionException session && session.Failure is QqqSessionException.FailureKind.AuthenticationRequired or QqqSessionException.FailureKind.PermissionDenied)
               { BlockAuthorization(); }
            }
            WriteLog("ERROR", "Realtime connection stopped: " + exception.Message);
         }
         finally
         {
            lock(_stateLock)
            { if(generation == _connectionGeneration) { _realtime = null; _realtimeReady = false; _realtimeLifetime = null; } }
            SignalPipeline();
         }
         RealtimeSettings settings = _realtimeSettings;
         await _reconnectRequested.WaitAsync(ReconnectDelay(settings, _reconnectAttempts++, Random.Shared.NextDouble()), token).ConfigureAwait(false);
      }
   }



   /// <summary>
   /// Exponential backoff from the configured minimum, capped at the maximum, with symmetric jitter of the configured
   /// percentage so many agents do not reconnect in lockstep after a server restart. The caller supplies the random
   /// sample so the schedule can be tested exactly; the result always stays inside the configured bounds.
   /// </summary>
   internal static TimeSpan ReconnectDelay(RealtimeSettings settings, int attempt, double sample)
   {
      double delay = Math.Min(settings.ReconnectMaxDelayMillis, settings.ReconnectMinDelayMillis * Math.Pow(2, Math.Clamp(attempt, 0, 12)));
      delay *= 1 + ((Math.Clamp(sample, 0, 1) * 2 - 1) * settings.ReconnectJitterPercent / 100);
      return TimeSpan.FromMilliseconds(Math.Clamp(delay, settings.ReconnectMinDelayMillis, settings.ReconnectMaxDelayMillis));
   }



   /// <summary>
   /// Runs one socket from upgrade to close. The reader, state applier, command worker, heartbeat, synchronizer and
   /// handshake watchdog start together, HELLO carries the capabilities discovered for this socket, and the first task to
   /// finish ends the connection. Teardown clears readiness before cancelling, so no claim can start on a dying socket.
   /// </summary>
   private async Task RealtimeConnectionAsync(long generation, CancellationToken token)
   {
      using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
      lock(_stateLock)
      {
         if(generation != _connectionGeneration || _transitioning)
         { return; }
         _realtimeLifetime = lifetime;
      }
      RealtimeSettings settings = _realtimeSettings;
      using var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
      handshake.CancelAfter(settings.WebSocketHandshakeTimeoutMillis);
      (WebSocket socket, bool supportsMedia) = await ConnectRealtimeSocketAsync(settings, lifetime, handshake.Token).ConfigureAwait(false);
      using var channel = new PrintingRealtimeChannel(socket, settings, _traffic);
      var updates = Channel.CreateBounded<RealtimeStateUpdate>(4096);
      var commands = Channel.CreateBounded<RealtimeEnvelope>(4096);
      _inboundBytes = 0;
      _inboundFrames = 0;
      _inventoryReady = false;
      lock(_stateLock)
      { _realtime = channel; }
      Task reader = channel.RunAsync(envelope =>
      {
         lock(_stateLock)
         {
            if(generation != _connectionGeneration)
            { throw new OperationCanceledException(lifetime.Token); }
            ReceiveRealtime(envelope, updates.Writer, commands.Writer);
         }
      }, lifetime.Token);
      Task state = ApplyRealtimeUpdatesAsync(updates.Reader, channel, generation, lifetime.Token);
      Task control = RealtimeCommandsAsync(commands.Reader, lifetime.Token);
      Task heartbeat = RealtimeHeartbeatAsync(channel, lifetime.Token);
      Task synchronize = RealtimeSynchronizeAsync(lifetime.Token);
      Task handshakeWatch = AwaitRealtimeHandshakeAsync(handshake.Token, lifetime.Token);
      try
      {
         await channel.SendAsync("HELLO", new
         {
            agentVersion = PrintApiClient.AgentVersion,
            capabilities = supportsMedia ? [.. RealtimeCapabilities, "printer-media-v1"] : RealtimeCapabilities,
            lastAppliedRevision = _controlState.DesiredState.Revision,
            lastSettingsRevision = settings.Revision,
            limits = RealtimeSettings.ClientLimits,
         }, token: lifetime.Token).ConfigureAwait(false);
         await Task.WhenAny(reader, state, control, heartbeat, synchronize, handshakeWatch).ConfigureAwait(false);
      }
      finally
      {
         lock(_stateLock)
         {
            _realtimeLifetime = null;
            _realtimeReady = false;
            _realtime = null;
         }
         SignalPipeline();
         await lifetime.CancelAsync().ConfigureAwait(false);
         await Task.WhenAll(reader, state, control, heartbeat, synchronize, handshakeWatch).ConfigureAwait(false);
      }
   }



   /// <summary>Reads the discovery refreshed by this socket establishment, so server upgrades and downgrades change capabilities on reconnect without polling.</summary>
   private async Task<(WebSocket Socket, bool SupportsMedia)> ConnectRealtimeSocketAsync(RealtimeSettings settings, CancellationTokenSource lifetime, CancellationToken token)
   {
      try
      {
         IQqqSessionClient session = _sessionClient ?? throw new InvalidOperationException("Sign in is required.");
         WebSocket socket = await session.ConnectPrintingAsync(
            TimeSpan.FromSeconds(settings.HeartbeatIntervalSeconds), TimeSpan.FromSeconds(settings.HeartbeatTimeoutSeconds), token).ConfigureAwait(false);
         try
         {
            QqqApiDiscovery discovery = await session.DiscoverAsync(token).ConfigureAwait(false);
            return (socket, discovery.SupportsPrinterMedia());
         }
         catch { socket.Dispose(); throw; }
      }
      catch
      {
         lock(_stateLock)
         { if(ReferenceEquals(_realtimeLifetime, lifetime)) { _realtimeLifetime = null; } }
         throw;
      }
   }



   /// <summary>
   /// Fails the connection when the server does not make the channel ready within the handshake timeout: it waits for
   /// readiness on the pipeline signal under the handshake token, and once ready idles until the connection's own
   /// lifetime ends so the connection is not torn down by this watchdog finishing.
   /// </summary>
   private async Task AwaitRealtimeHandshakeAsync(CancellationToken handshake, CancellationToken lifetime)
   {
      while(true)
      {
         Task changed;
         lock(_stateLock)
         {
            if(_realtimeReady)
            { break; }
            changed = _pipelineChanged.Task;
         }
         await changed.WaitAsync(handshake).ConfigureAwait(false);
      }
      await Task.Delay(Timeout.InfiniteTimeSpan, lifetime).ConfigureAwait(false);
   }



   /// <summary>
   /// Handles one inbound frame on the reader's thread. It records contact, tracks the server clock offset from WELCOME
   /// and PONG, and routes state to the ordered applier, commands to the command worker and job notifications to a
   /// claim wake-up. The queues are bounded and overflow closes the connection rather than silently dropping a frame,
   /// and an ERROR frame that means authorization is gone blocks further attempts.
   /// </summary>
   private void ReceiveRealtime(RealtimeEnvelope envelope, ChannelWriter<RealtimeStateUpdate> updates, ChannelWriter<RealtimeEnvelope> commands)
   {
      lock(_stateLock)
      { _lastSuccessfulPoll = ObservationTime; _heartbeatFailure = null; }
      if(envelope.Type is "WELCOME" or "PONG")
      {
         string? serverTime = envelope.Payload.GetProperty("serverTime").GetString();
         if(!DateTimeOffset.TryParse(serverTime, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTimeOffset server))
         { throw new InvalidDataException("Invalid realtime server time."); }
         _serverTimeOffset = server - DateTimeOffset.UtcNow;
      }
      switch(envelope.Type)
      {
         case "WELCOME":
         case "DESIRED_STATE":
         case "SETTINGS":
            ApplyImmediateRestriction(envelope);
            ReserveInbound(envelope);
            if(!updates.TryWrite(new RealtimeStateUpdate(envelope, ++_stateSequence)))
            { throw new IOException("Realtime state queue is full."); }
            break;
         case "COMMAND":
            DeviceControlCommand command = envelope.Payload.Deserialize<DeviceControlCommand>(PrintApiClient.JsonOptions) ?? throw new InvalidDataException("Missing command.");
            PrintApiClient.ValidateRealtimeCommand(command);
            ReserveInbound(envelope);
            if(!commands.TryWrite(envelope))
            { throw new IOException("Realtime command queue is full."); }
            break;
         case "JOB_AVAILABLE":
            WakePoll();
            RequestQueueRefresh();
            break;
         case "PONG":
            break;
         case "ERROR":
            string code = envelope.Payload.GetProperty("code").GetString() ?? "UNKNOWN";
            if(code is "AUTHENTICATION_REQUIRED" or "PERMISSION_DENIED" or "AUTHORIZATION_REQUIRED")
            { BlockAuthorization(); }
            throw new IOException("Realtime server response: " + code);
         default:
            throw new InvalidDataException("Unsupported realtime message.");
      }
   }



   /// <summary>
   /// Applies the safe half of a state change the moment it arrives, before it is persisted: new settings or a new
   /// desired state make the channel not ready, so no new claim starts until the update is durably applied and
   /// acknowledged. A regressed or invalid revision is a protocol violation and throws rather than being ignored.
   /// </summary>
   private void ApplyImmediateRestriction(RealtimeEnvelope envelope)
   {
      if(string.Equals(envelope.Type, "SETTINGS", StringComparison.Ordinal))
      {
         lock(_stateLock)
         { _realtimeReady = false; }
         SignalPipeline();
         return;
      }
      JsonElement value = string.Equals(envelope.Type, "WELCOME", StringComparison.Ordinal) ? envelope.Payload.GetProperty("desiredState") : envelope.Payload;
      DeviceDesiredState desired = value.Deserialize<DeviceDesiredState>(PrintApiClient.JsonOptions) ?? throw new InvalidDataException("Missing desired state.");
      if(desired.Revision < 0 || desired.ConnectionState is not ("CONNECTED" or "DISCONNECTED"))
      { throw new InvalidDataException("Invalid desired state."); }
      lock(_stateLock)
      {
         if(desired.Revision < _controlState.DesiredState.Revision || (desired.Revision == _controlState.DesiredState.Revision && desired != _controlState.DesiredState))
         { throw new InvalidDataException("Desired state revision regressed."); }
         _controlState = _controlState with { Support = "SUPPORTED", DesiredState = desired, LastContact = Now() };
         _realtimeReady = false;
      }
      SignalPipeline();
   }



   /// <summary>
   /// Persists and acknowledges server state in order. Settings and control state are saved under the connection gate,
   /// STATE_APPLIED reports the revisions now in force, and only the newest update on the current generation marks the
   /// runtime ready and resets the backoff. The status, queue preview and (for WELCOME and DESIRED_STATE) claim drain
   /// are then woken.
   /// </summary>
   private async Task ApplyRealtimeUpdatesAsync(ChannelReader<RealtimeStateUpdate> updates, PrintingRealtimeChannel channel, long generation, CancellationToken token)
   {
      await foreach(RealtimeStateUpdate update in updates.ReadAllAsync(token).ConfigureAwait(false))
      {
         RealtimeEnvelope envelope = update.Envelope;
         ReleaseInbound(envelope);
         AgentControlState saved;
         await _connectionGate.WaitAsync(token).ConfigureAwait(false);
         try
         {
            lock(_stateLock)
            { if(generation != _connectionGeneration) { throw new OperationCanceledException(token); } }
            using var store = new AgentStore(directory);
            if(envelope.Type is "WELCOME" or "SETTINGS")
            {
               JsonElement value = string.Equals(envelope.Type, "WELCOME", StringComparison.Ordinal) ? envelope.Payload.GetProperty("settings") : envelope.Payload;
               var settings = RealtimeSettings.Parse(value);
               settings.Validate();
               if(settings.Revision < _realtimeSettings.Revision)
               { throw new InvalidDataException("Settings revision regressed."); }
               store.SaveRealtimeSettings(settings);
               _realtimeSettings = settings;
               channel.UpdateSettings(settings);
            }
            lock(_stateLock)
            { saved = _controlState; }
            store.SaveControlState(saved);
         }
         finally { _connectionGate.Release(); }
         await channel.SendAsync("STATE_APPLIED", new { lastAppliedRevision = saved.DesiredState.Revision, lastSettingsRevision = _realtimeSettings.Revision }, acknowledged: true, token: token).ConfigureAwait(false);
         lock(_stateLock)
         {
            if(generation == _connectionGeneration && update.Sequence == _stateSequence && saved.DesiredState.Revision == _controlState.DesiredState.Revision)
            {
               _realtimeReady = true;
               _transportConnectedBefore = true;
               _controlReady = true;
               _controlFailure = null;
               _failure = null;
               _reconnectAttempts = 0;
            }
         }
         SignalPipeline();
         RequestStatusSynchronization();
         RequestQueueRefresh();
         if(envelope.Type is "WELCOME" or "DESIRED_STATE")
         { WakePoll(); }
      }
   }
}
