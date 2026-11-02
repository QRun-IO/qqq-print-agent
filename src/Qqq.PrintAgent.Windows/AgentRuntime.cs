/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using KofTwentyTwo.AppKit.Logging;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>Hosts outbound printing and the local administrator management pipe.</summary>
public sealed partial class AgentRuntime(string directory, Action<string> log, IPrinterBackend? printerBackend = null,
   Func<AgentConfiguration, PrintApiClient>? apiFactory = null, string pipeName = "QqqPrintAgent", Func<NamedPipeServerStream>? pipeFactory = null,
   Func<Uri, IQqqSessionClient>? sessionFactory = null, TimeProvider? timeProvider = null) : IDisposable
{
   private readonly Lock _stateLock = new();
   private readonly IPrinterBackend _printers = printerBackend ?? new WindowsPrinters();
   private readonly Queue<AgentLogEntry> _logs = new();
   private readonly SemaphoreSlim _pollRequested = new(0, 1);
   private readonly SemaphoreSlim _connectionGate = new(1, 1);
   private readonly IActivityLog _activityLog = AgentActivityLog.Create(directory);
   private DateTimeOffset? _lastSuccessfulPoll;
   private string? _failure;
   private int _pollIntervalSeconds = 5;
   private IReadOnlyList<JobSnapshot> _queuedJobs = [];
   private long _queueGeneration;
   private DateTimeOffset _nextHistoryMaintenance;
   private bool _pollStarted;
   private DateTimeOffset ObservationTime => timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow;



   /// <summary>No persistent database handles are held by the runtime.</summary>
   public void Dispose()
   {
      _pollRequested.Dispose();
      _connectionGate.Dispose();
      _controlExecutionGate.Dispose();
      _authenticationGate.Dispose();
      _sessionClient?.Dispose();
      _reconnectRequested.Dispose();
      _eventDeliveryGate.Dispose();
      _claimGate.Dispose();
      _statusRequested.Dispose();
      _queueRefreshRequested.Dispose();
   }



   /// <summary>Owns the data directory until Windows requests shutdown.</summary>
   public async Task RunAsync(CancellationToken token)
   {
      Directory.CreateDirectory(directory);
      using FileStream instance = new(Path.Combine(directory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
      WriteLog("INFO", "Print service started.");
      if(apiFactory is null)
      {
         await RunRealtimeAsync(token).ConfigureAwait(false);
         return;
      }
      // Explicit injected HTTP fixtures retain their deterministic test harness;
      // production native sessions never enter the legacy polling path.
      using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
      Task control = ControlLoopAsync(lifetime.Token);
      Task poll = PollAsync(lifetime.Token);
      Task pipe = ServePipeAsync(lifetime.Token);
      Task heartbeat = HeartbeatAsync(lifetime.Token);
      await Task.WhenAny(poll, pipe, heartbeat, control).ConfigureAwait(false);
      await lifetime.CancelAsync().ConfigureAwait(false);
      await Task.WhenAll(poll, pipe, heartbeat, control).ConfigureAwait(false);
   }



   /// <summary>
   /// Legacy claim loop used only with an injected HTTP API (fixtures); production sessions never enter it. It runs one
   /// pass at a time under the connection gate so updates and enrollment are not starved, yields between jobs, records a
   /// failure once instead of on every retry, and otherwise sleeps until the next interval or an explicit wake-up.
   /// </summary>
   private async Task PollAsync(CancellationToken token)
   {
      lock(_stateLock)
      {
         _pollStarted = true;
      }
      while(!token.IsCancellationRequested)
      {
         bool processed = false;
         try
         {
            await _connectionGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
               processed = await PollOnceAsync(token).ConfigureAwait(false);
            }
            finally { _connectionGate.Release(); }
         }
         catch(Exception exception) when(exception is not OperationCanceledException || !token.IsCancellationRequested)
         {
            bool changed;
            lock(_stateLock)
            {
               changed = !string.Equals(_failure, exception.Message, StringComparison.Ordinal);
               _failure = exception.Message;
            }
            if(changed)
            { WriteLog("ERROR", exception.Message); }
         }
         if(processed)
         {
            // Release the connection gate and yield between jobs so update/enrollment
            // commands and the independent heartbeat cannot starve behind a backlog.
            await Task.Yield();
         }
         else
         {
            await _pollRequested.WaitAsync(TimeSpan.FromSeconds(_pollIntervalSeconds), token).ConfigureAwait(false);
         }
      }
   }



   /// <summary>
   /// One legacy claim pass: skipped while an update pauses printing, authorization is missing or the server demands it.
   /// It refreshes the queued-job preview only when traffic is allowed and discards the result if the queue generation
   /// moved meanwhile, then lets the worker claim at most one job against the exposed printers and prunes history once a minute.
   /// </summary>
   private async Task<bool> PollOnceAsync(CancellationToken token)
   {
      if(UpdatePaused || (apiFactory is null && !_authorizationReady))
      {
         return false;
      }
      lock(_stateLock)
      {
         if(_controlState.AuthorizationRequired)
         {
            return false;
         }
      }
      using AgentStore store = new(directory);
      AgentConfiguration? configuration = store.LoadConfiguration();
      if(configuration is null)
      { return false; }
      _pollIntervalSeconds = Math.Clamp(configuration.PollIntervalSeconds, 1, 20);
      using PrintApiClient api = CreateApi(configuration);
      long queueGeneration;
      lock(_stateLock)
      {
         queueGeneration = _queueGeneration;
      }
      IReadOnlyList<PrintJob> queued = NormalTrafficAllowed ? await api.QueuedJobsAsync(token).ConfigureAwait(false) : [];
      lock(_stateLock)
      {
         if(queueGeneration == _queueGeneration)
         {
            _queuedJobs = queued.Select(job => new JobSnapshot(job.JobId, "QUEUED", job.CreatedAt, "Waiting on the server; not yet received by this device.", null, job.PrinterId, _queuedJobs.FirstOrDefault(previous => string.Equals(previous.PrinterId, job.PrinterId, StringComparison.Ordinal))?.PrinterName, job.Title, job.Format, CreatedAt: JobSorting.NormalizeCreatedAt(job.CreatedAt))).ToArray();
         }
      }
      ExposedPrinterBackend exposed = new(new ProfiledPrinterBackend(_printers, store), store.GetExposedPrinterIds);
      bool processed = await new PrintWorker(api, exposed, store, message => WriteLog("INFO", message), () => DispatchAllowed, () => NormalTrafficAllowed).RunOnceAsync(token).ConfigureAwait(false);
      if(DateTimeOffset.UtcNow >= _nextHistoryMaintenance)
      {
         _ = store.PruneHistory();
         _nextHistoryMaintenance = DateTimeOffset.UtcNow.AddMinutes(1);
      }
      bool recovered;
      lock(_stateLock)
      {
         recovered = _failure is not null || _lastSuccessfulPoll is null;
         _failure = null;
      }
      if(recovered)
      { WriteLog("INFO", "Connected to the print server."); }
      return processed;
   }



   /// <summary>
   /// Builds the API client for one unit of work: the injected one in tests, otherwise a client bound to the live QQQ
   /// session and the current realtime channel. It fails when no channel exists, so no HTTP-only path can claim work.
   /// </summary>
   private PrintApiClient CreateApi(AgentConfiguration configuration)
   {
      if(apiFactory is not null)
      { return apiFactory(configuration); }
      PrintingRealtimeChannel realtime;
      lock(_stateLock)
      { realtime = _realtime ?? throw new IOException("The realtime connection is unavailable."); }
      return PrintApiClient.ForSession(_sessionClient ?? throw new InvalidOperationException("Sign in to QQQ before printing.")).WithRealtime(realtime, _realtimeSettings, CurrentRealtime);
   }



   /// <summary>
   /// Returns the channel of the connection in force at call time. It is passed as a callback so a client built for one
   /// connection reads the replacement after a reconnect instead of sending on a closed socket, and it throws while none exists.
   /// </summary>
   private PrintingRealtimeChannel CurrentRealtime()
   {
      lock(_stateLock)
      { return _realtime ?? throw new IOException("Realtime connection unavailable; durable receipts are retained."); }
   }



   /// <summary>
   /// Wakes the claim loop. The single-slot semaphore coalesces repeated wake-ups into one pass, so a burst of job
   /// notifications cannot queue unbounded work; the status and queue-preview refreshes are requested alongside.
   /// </summary>
   private void WakePoll()
   {
      if(apiFactory is null)
      { RequestStatusSynchronization(); RequestQueueRefresh(); }
      lock(_stateLock)
      {
         if(_pollRequested.CurrentCount == 0)
         { _pollRequested.Release(); }
      }
   }



   /// <summary>Current local observations, with no token or document content.</summary>
   public AgentSnapshot GetSnapshot()
   {
      using AgentStore store = new(directory, recoverUnreadableConfiguration: true);
      AgentConfiguration? configuration = store.LoadConfiguration();
      IReadOnlySet<string> exposed = store.GetExposedPrinterIds();
      var printers = new ProfiledPrinterBackend(_printers, store).Discover().Select(printer => new PrinterSnapshot(printer.PrinterId, printer.Name, printer.DriverName, printer.PortName, printer.Status, printer.Formats, exposed.Contains(printer.PrinterId), printer.DisplayName, printer.Description, printer.Location, printer.MediaProfiles)).ToList();
      foreach(string missing in exposed.Where(id => !printers.Any(printer => string.Equals(printer.PrinterId, id, StringComparison.Ordinal))))
      {
         PrinterProfile profile = store.GetPrinterProfile(missing);
         printers.Add(new PrinterSnapshot(missing, "Previously exposed printer is unavailable", "", "", "OFFLINE", [], true, profile.DisplayName, profile.Description, profile.Location));
      }
      IReadOnlyList<JobSnapshot> jobs = store.RecentJobs();
      int attentionCount = store.NeedsAttentionCount();
      lock(_stateLock)
      {
         _queuedJobs = _queuedJobs.Select(job =>
         {
            PrinterSnapshot? printer = printers.FirstOrDefault(item => string.Equals(item.PrinterId, job.PrinterId, StringComparison.Ordinal));
            return printer is null ? job : job with { PrinterName = string.IsNullOrWhiteSpace(printer.DisplayName) ? printer.Name : printer.DisplayName };
         }).ToArray();
         jobs = jobs.Concat(_queuedJobs.Where(queued => !jobs.Any(local => string.Equals(local.JobId, queued.JobId, StringComparison.Ordinal))))
            .Select(job => job.PrinterName is null ? job with { PrinterName = printers.FirstOrDefault(printer => string.Equals(printer.PrinterId, job.PrinterId, StringComparison.Ordinal))?.Name } : job).ToArray();
         bool connected = configuration is not null && NormalTrafficAllowed && _failure is null && _heartbeatFailure is null && _lastSuccessfulPoll is not null
            && ObservationTime - _lastSuccessfulPoll.Value < TimeSpan.FromSeconds(apiFactory is null ? _realtimeSettings.HeartbeatTimeoutSeconds : 30);
         bool printerIssue = !printers.Any(printer => printer.Exposed) || printers.Any(printer => printer.Exposed && !string.Equals(printer.Status, "READY", StringComparison.Ordinal));
         bool jobFailure = attentionCount > 0;
         string health = !connected || jobFailure ? "RED" : printerIssue ? "ORANGE" : "GREEN";
         string status = configuration is null ? "Not enrolled" : _failure ?? _heartbeatFailure ?? (!connected ? "Waiting for server connection" : jobFailure ? $"Connected; {attentionCount} job(s) need review" : printerIssue ? "Connected; exposed printers need attention" : "Connected; exposed printers ready");
         if(_updatePauseUntil > DateTimeOffset.UtcNow)
         {
            status = "Printing paused for application update. " + status;
         }
         (health, status) = ControlStatus(health, status, jobFailure);
         if(apiFactory is null && !_authorizationReady)
         {
            health = "RED";
            status = _authenticationMessage;
         }
         return new AgentSnapshot(configuration is not null, connected, health, status, configuration?.ServerUrl, configuration?.DeviceId, _lastSuccessfulPoll?.ToString("O", System.Globalization.CultureInfo.InvariantCulture), printers, jobs, _logs.ToArray(), store.GetDeviceProfile(), _controlState, AuthenticationStatus(), GetTransportSnapshot());
      }
   }



   /// <summary>
   /// Records a service message in the in-memory ring shown to the companion (newest 100) and in the shared activity log.
   /// The text is redacted first and capped at 1000 characters, so credentials and document content cannot reach either sink.
   /// </summary>
   private void WriteLog(string level, string message)
   {
      string sanitized = AgentActivityLog.Redact(message);
      string bounded = sanitized.Length <= 1000 ? sanitized : sanitized[..1000];
      lock(_stateLock)
      {
         _logs.Enqueue(new AgentLogEntry(DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture), level, bounded));
         while(_logs.Count > 100)
         {
            _logs.Dequeue();
         }
      }
      if(string.Equals(level, "ERROR", StringComparison.Ordinal))
      { _activityLog.Error("[Service] " + bounded); }
      else
      { _activityLog.Info("[Service] " + bounded); }
      log(bounded);
   }



   /// <summary>
   /// Serves the local administrator pipe one client at a time, each command under a timeout (longer for sign-in and
   /// enrollment). A failing or vanished client gets a short, redacted error and never stops the pipe; an unreadable saved
   /// connection gets the recovery instructions instead of a raw cryptographic message. The connection is always
   /// disconnected afterwards so a peer that closed mid-command cannot leave the pipe broken.
   /// </summary>
   private async Task ServePipeAsync(CancellationToken token)
   {
      await using NamedPipeServerStream pipe = pipeFactory?.Invoke() ?? NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, CreatePipeSecurity());
      while(!token.IsCancellationRequested)
      {
         await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);

         try
         {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            string line = await AgentPipeFraming.ReadAsync(pipe, AgentPipeFraming.MaximumCommandCharacters, timeout.Token).ConfigureAwait(false);
            PipeRequest request = JsonSerializer.Deserialize<PipeRequest>(line, PrintApiClient.JsonOptions) ?? throw new InvalidDataException("Invalid command.");
            timeout.CancelAfter(TimeSpan.FromSeconds(request.Operation is "enroll" or "sign-in" or "sign-out" ? 120 : 30));
            await AgentPipeFraming.WriteResponseAsync(pipe, await HandleCommandAsync(request, timeout.Token).ConfigureAwait(false), timeout.Token).ConfigureAwait(false);
         }
         catch(Exception exception) when(exception is not OperationCanceledException || !token.IsCancellationRequested)
         {
            WriteLog("ERROR", "Local command failed: " + exception.Message);
            string message = exception is System.Security.Cryptography.CryptographicException
               ? "The saved connection cannot be opened. Open Settings, choose Connect this device or Sign in again, and use a fresh server enrollment code. Existing job history will be preserved."
               : AgentActivityLog.Redact(exception.Message);
            try
            {
               using var errorTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
               errorTimeout.CancelAfter(TimeSpan.FromSeconds(1));
               await AgentPipeFraming.WriteResponseAsync(pipe, "Error: " + message, errorTimeout.Token).ConfigureAwait(false);
            }
            catch(Exception writeException) when(writeException is IOException or OperationCanceledException)
            { /* An unresponsive client must not stop the management pipe. */ }
         }
         finally
         {
            // Disconnect also resets the Broken state after a peer closes mid-command.
            pipe.Disconnect();
         }
      }
   }



   /// <summary>
   /// Access control for the management pipe: LocalSystem has full control and Administrators read and write, and nobody
   /// else, so only an elevated operator can change exposure, enrollment or credentials.
   /// </summary>
   internal static PipeSecurity CreatePipeSecurity()
   {
      PipeSecurity security = new();
      security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null), PipeAccessRights.FullControl, AccessControlType.Allow));
      security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
      return security;
   }



   /// <summary>
   /// Dispatches one management-pipe operation by name to its handler and returns the text sent back to the caller. An
   /// unknown operation is refused, and exposure changes validate their arguments before touching the store.
   /// </summary>
   private async Task<string> HandleCommandAsync(PipeRequest request, CancellationToken token)
   {
      switch(request.Operation)
      {
         case "set-device-profile":
         case "set-printer-profile":
         case "get-device-profile":
         case "get-log-settings":
         case "set-log-settings":
         case "clear-logs":
            return HandleSettingsCommand(request);
         case "prepare-update":
            return await PrepareUpdateAsync(token).ConfigureAwait(false);
         case "resume-after-update":
            return ResumeAfterUpdate();
         case "status":
            return GetSnapshot().Status;
         case "snapshot":
            return JsonSerializer.Serialize(GetSnapshot(), PrintApiClient.JsonOptions);
         case "transport":
            return JsonSerializer.Serialize(GetTransportSnapshot(), PrintApiClient.JsonOptions);
         case "printers":
            return JsonSerializer.Serialize(_printers.Discover(), PrintApiClient.JsonOptions);
         case "set-exposure":
            if(string.IsNullOrWhiteSpace(request.PrinterId) || request.Exposed is null)
            {
               throw new InvalidOperationException("PrinterId and Exposed are required.");
            }
            using(AgentStore store = new(directory))
            {
               store.SetExposure(request.PrinterId, request.Exposed.Value, _printers.Discover());
            }
            WakePoll();
            WriteLog("INFO", request.Exposed.Value ? "A printer was shared with the server." : "A printer was hidden from the server.");
            return JsonSerializer.Serialize(GetSnapshot(), PrintApiClient.JsonOptions);
         case "enroll":
            return await EnrollWithGatesAsync(request, token).ConfigureAwait(false);
         case "sign-in":
            return await SignInAsync(request, token).ConfigureAwait(false);
         case "sign-out":
            return await SignOutAsync(token).ConfigureAwait(false);
         case "reconnect":
            return ReconnectLocal();
         case "test-print":
         case "retry-job":
         case "cancel-job":
         case "acknowledge-job":
         case "job-details":
         case "query-jobs":
            return await HandleJobCommandAsync(request, token).ConfigureAwait(false);
         default:
            throw new InvalidOperationException("Unknown command.");
      }
   }



   /// <summary>Compatibility entry point for enrollment; the native session path in <c>EnrollNativeAsync</c> does the work.</summary>
   private async Task<string> EnrollAsync(PipeRequest request, CancellationToken token)
   {
      return await EnrollNativeAsync(request, token).ConfigureAwait(false);
   }
}

