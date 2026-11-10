/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Qqq.PrintAgent.Core;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Hosts outbound printing and the local administrator management pipe. The service reaches QQQ in one way only: a signed-in
/// session and the realtime channel opened through it. The printer backend, pipe, session client and clock can be replaced,
/// which is how tests run this same path against a scripted server.
/// </summary>
public sealed partial class AgentRuntime(string directory, Action<string> log, IPrinterBackend? printerBackend = null,
   string pipeName = "QqqPrintAgent", Func<NamedPipeServerStream>? pipeFactory = null,
   Func<Uri, IQqqSessionClient>? sessionFactory = null, TimeProvider? timeProvider = null) : IDisposable
{
   private readonly Lock _stateLock = new();
   private readonly IPrinterBackend _printers = printerBackend ?? new WindowsPrinters();
   private readonly Queue<AgentLogEntry> _logs = new();
   private readonly SemaphoreSlim _pollRequested = new(0, 1);
   private readonly SemaphoreSlim _connectionGate = new(1, 1);
   private readonly AgentActivityLog _activityLog = AgentActivityLog.Create(directory);
   private DateTimeOffset? _lastSuccessfulPoll;
   private IReadOnlyList<JobSnapshot> _queuedJobs = [];
   private long _queueGeneration;
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
      await RunRealtimeAsync(token).ConfigureAwait(false);
   }



   /// <summary>
   /// Builds the API client for one unit of work, bound to the live QQQ session and the current realtime channel. It fails
   /// when no channel exists, so no HTTP-only path can claim work.
   /// </summary>
   private PrintApiClient CreateApi()
   {
      PrintingRealtimeChannel realtime;
      lock(_stateLock)
      { realtime = _realtime ?? throw new IOException("The realtime connection is unavailable."); }
      return PrintApiClient.ForSession(_sessionClient ?? throw new InvalidOperationException($"Sign in to {AppIdentity.ServerName} before printing.")).WithRealtime(realtime, _realtimeSettings, CurrentRealtime);
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
      RequestStatusSynchronization();
      RequestQueueRefresh();
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
      var printers = new ProfiledPrinterBackend(_printers, store).Discover().Select(printer => new PrinterSnapshot(printer.PrinterId, printer.Name, printer.DriverName, printer.PortName, printer.Status, printer.Formats, exposed.Contains(printer.PrinterId), printer.DisplayName, printer.Description, printer.Location, printer.MediaProfiles, ServerStateFor(printer.PrinterId, exposed.Contains(printer.PrinterId)))).ToList();
      foreach(string missing in exposed.Where(id => !printers.Any(printer => string.Equals(printer.PrinterId, id, StringComparison.Ordinal))))
      {
         PrinterProfile profile = store.GetPrinterProfile(missing);
         printers.Add(new PrinterSnapshot(missing, "Previously exposed printer is unavailable", "", "", "OFFLINE", [], true, profile.DisplayName, profile.Description, profile.Location, ServerState: ServerStateFor(missing, exposed: true)));
      }
      PrinterServerSync printerSync;
      lock(_stateLock)
      { printerSync = _catalogSync; }
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
         bool connected = configuration is not null && NormalTrafficAllowed && _heartbeatFailure is null && _lastSuccessfulPoll is not null
            && ObservationTime - _lastSuccessfulPoll.Value < TimeSpan.FromSeconds(_realtimeSettings.HeartbeatTimeoutSeconds);
         bool printerIssue = !printers.Any(printer => printer.Exposed) || printers.Any(printer => printer.Exposed && !string.Equals(printer.Status, "READY", StringComparison.Ordinal));
         bool jobFailure = attentionCount > 0;
         string health = !connected || jobFailure ? "RED" : printerIssue ? "ORANGE" : "GREEN";
         string status = configuration is null ? "Not enrolled" : _heartbeatFailure ?? (!connected ? "Waiting for server connection" : jobFailure ? $"Connected; {attentionCount} job(s) need review" : printerIssue ? "Connected; exposed printers need attention" : "Connected; exposed printers ready");
         if(_updatePauseUntil > DateTimeOffset.UtcNow)
         {
            status = "Printing paused for application update. " + status;
         }
         (health, status) = ControlStatus(health, status, jobFailure);
         if(!_authorizationReady)
         {
            health = "RED";
            status = _authenticationMessage;
         }
         return new AgentSnapshot(configuration is not null, connected, health, status, configuration?.ServerUrl, configuration?.DeviceId, _lastSuccessfulPoll?.ToString("O", System.Globalization.CultureInfo.InvariantCulture), printers, jobs, _logs.ToArray(), store.GetDeviceProfile(), _controlState, AuthenticationStatus(), GetTransportSnapshot(), PrinterSync: printerSync);
      }
   }



   /// <summary>
   /// Records a service message in the in-memory ring shown to the companion (newest 100) and in the shared activity log.
   /// The text is redacted first and capped at 1000 characters (4000 for debug detail), so credentials and document content
   /// cannot reach either sink. DEBUG entries go only to the file, only while debug logging is on, and never displace the
   /// ring's operational entries.
   /// </summary>
   private void WriteLog(string level, string message)
   {
      bool debug = string.Equals(level, "DEBUG", StringComparison.Ordinal);
      if(debug && !_activityLog.IsDebugEnabled)
      { return; }
      string sanitized = AgentActivityLog.Redact(message);
      int cap = debug ? 4000 : 1000;
      string bounded = sanitized.Length <= cap ? sanitized : sanitized[..cap];
      if(debug)
      {
         _activityLog.Debug("[Service] " + bounded);
         return;
      }
      lock(_stateLock)
      {
         _logs.Enqueue(new AgentLogEntry(DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture), level, bounded));
         while(_logs.Count > 100)
         {
            _logs.Dequeue();
         }
      }
      switch(level)
      {
         case "ERROR":
            _activityLog.Error("[Service] " + bounded);
            break;
         case "WARN":
            _activityLog.Warning("[Service] " + bounded);
            break;
         default:
            _activityLog.Info("[Service] " + bounded);
            break;
      }
      log(bounded);
   }



   /// <summary>Records a failure with its type and message at ERROR, and the full exception (stack and inner causes) as debug detail.</summary>
   private void WriteFailure(string context, Exception exception)
   {
      WriteLog("ERROR", context + ": " + QqqSessionDiagnostics.Describe(exception));
      WriteLog("DEBUG", context + " detail: " + exception);
   }



   /// <summary>The session client's progress sink: step entries at INFO, request detail at DEBUG, both through the redacting service log.</summary>
   private QqqSessionDiagnostics SessionDiagnostics() => new(message => WriteLog("INFO", "[Session] " + message),
      message => WriteLog("DEBUG", "[Session] " + message), () => _activityLog.IsDebugEnabled);



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
            WriteLog("ERROR", "Local command failed: " + QqqSessionDiagnostics.Describe(exception));
            string message = exception is System.Security.Cryptography.CryptographicException
               ? "The saved connection cannot be opened. Open Settings, choose Connect this device or Sign in again, and use a fresh server enrollment code. Existing job history will be preserved."
               : AgentActivityLog.Redact(QqqSessionDiagnostics.Explain(exception));
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
         case "get-server-trust":
         case "set-server-trust":
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
            return await SignOutAsync(request.PostLogoutRedirectUri, token).ConfigureAwait(false);
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
}

