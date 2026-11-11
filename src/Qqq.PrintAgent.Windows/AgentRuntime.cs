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
   private readonly ServiceLog _log = new(AgentActivityLog.Create(directory), log);
   private readonly SemaphoreSlim _claimsRequested = new(0, 1);
   private readonly SemaphoreSlim _connectionGate = new(1, 1);
   private DateTimeOffset? _lastServerContact;
   private IReadOnlyList<JobSnapshot> _queuedJobs = [];
   private long _queueGeneration;
   private StateStatus _storedState = new StateStatus.Ready();
   private TimeProvider Clock => timeProvider ?? TimeProvider.System;
   private DateTimeOffset ObservationTime => Clock.GetUtcNow();



   /// <summary>No persistent database handles are held by the runtime.</summary>
   public void Dispose()
   {
      _claimsRequested.Dispose();
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



   /// <summary>
   /// Owns the data directory until Windows requests shutdown. The journal is migrated first, once, before anything opens
   /// a store. When the stored state cannot be used (a newer build wrote it), it is left exactly as it was found, no
   /// connection to a server is started, and only the management pipe is served, so that an operator can see why and the
   /// newer build can be installed again.
   /// </summary>
   public async Task RunAsync(CancellationToken token)
   {
      Directory.CreateDirectory(directory);
      using FileStream instance = new(Path.Combine(directory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
      _log.Write("INFO", "Print service started.");
      _storedState = MigrateJournal();
      if(_storedState.Refusal is string refusal)
      {
         _log.Write("ERROR", refusal + (_storedState is StateStatus.NewerBuild newer ? $" The journal is at schema version {newer.FoundVersion}; this version supports up to {newer.SupportedVersion}." : string.Empty)
            + " No server connection is started; status, log settings and updates stay available.");
         await ServePipeAsync(token).ConfigureAwait(false);
         return;
      }
      await RunRealtimeAsync(token).ConfigureAwait(false);
   }



   /// <summary>
   /// Brings the journal up to date, which is the first thing the service does with its stored state. A failure stops
   /// the service from starting, as a failed first open of the store did before there was a migration. Before the
   /// failure is passed on, one line is logged for the operator: which journal it was, that it was not changed, and
   /// what to check.
   /// </summary>
   private StateStatus MigrateJournal()
   {
      try
      {
         return JournalSchema.Migrate(directory);
      }
      catch(Exception exception)
      {
         _log.Write("ERROR", JournalSchema.StartFailureText(directory, exception));
         throw;
      }
   }



   /// <summary>
   /// Opens the journal and the protected files of the state directory for one unit of work. Every part of the runtime
   /// opens its store here, so what a store is opened for is decided in one place. With <paramref name="recover"/> a saved
   /// connection that can no longer be read is set aside instead of failing the open, which the paths that must keep
   /// working without a readable pairing (status, sign-in, enrollment, settings) ask for. While the stored state cannot be
   /// used no store is opened at all, whatever asked for one: that state belongs to another build.
   /// </summary>
   private AgentStore OpenStore(bool recover) => _storedState.Refusal is string refusal
      ? throw new InvalidOperationException(refusal)
      : new(directory, recoverUnreadableConfiguration: recover);



   /// <summary>
   /// Builds the API client for one unit of work, bound to the live QQQ session and the current realtime channel. It fails
   /// when no channel exists, so no HTTP-only path can claim work, and it fails with the channel-closed type, because the
   /// caller only found the connection gone and that is never the reason the connection ended.
   /// </summary>
   private PrintApiClient CreateApi()
   {
      PrintingRealtimeChannel realtime;
      lock(_stateLock)
      { realtime = _realtime ?? throw new RealtimeChannelClosedException("The realtime connection is unavailable.", null); }
      return PrintApiClient.ForSession(_sessionClient ?? throw new InvalidOperationException($"Sign in to {AppIdentity.ServerName} before printing.")).WithRealtime(realtime, _realtimeSettings, CurrentRealtime);
   }



   /// <summary>
   /// Returns the channel of the connection in force at call time. It is passed as a callback so a client built for one
   /// connection reads the replacement after a reconnect instead of sending on a closed socket, and it throws while none exists.
   /// </summary>
   private PrintingRealtimeChannel CurrentRealtime()
   {
      lock(_stateLock)
      { return _realtime ?? throw new RealtimeChannelClosedException("Realtime connection unavailable; durable receipts are retained.", null); }
   }



   /// <summary>
   /// Wakes the claim loop. The single-slot semaphore coalesces repeated wake-ups into one pass, so a burst of job
   /// notifications cannot queue unbounded work; the status and queue-preview refreshes are requested alongside.
   /// </summary>
   private void WakeClaims()
   {
      RequestStatusSynchronization();
      RequestQueueRefresh();
      lock(_stateLock)
      {
         if(_claimsRequested.CurrentCount == 0)
         { _claimsRequested.Release(); }
      }
   }



   /// <summary>
   /// Current local observations, with no token or document content. While the stored state cannot be used the snapshot
   /// is built without it: red, with the reason as its status, no pairing, no printers and no jobs.
   /// </summary>
   public AgentSnapshot GetSnapshot()
   {
      if(_storedState.Refusal is string refusal)
      {
         return new AgentSnapshot(Enrolled: false, Connected: false, "RED", refusal, ServerUrl: null, DeviceId: null, LastSuccessfulPoll: null, [], [], _log.Entries(), Transport: GetTransportSnapshot());
      }
      using AgentStore store = OpenStore(recover: true);
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
         bool connected = configuration is not null && NormalTrafficAllowed && _heartbeatFailure is null && _lastServerContact is not null
            && ObservationTime - _lastServerContact.Value < TimeSpan.FromSeconds(_realtimeSettings.HeartbeatTimeoutSeconds);
         bool printerIssue = !printers.Any(printer => printer.Exposed) || printers.Any(printer => printer.Exposed && !string.Equals(printer.Status, "READY", StringComparison.Ordinal));
         bool jobFailure = attentionCount > 0;
         string health = !connected || jobFailure ? "RED" : printerIssue ? "ORANGE" : "GREEN";
         string status = configuration is null ? "Not enrolled" : _heartbeatFailure ?? (!connected ? "Waiting for server connection" : jobFailure ? $"Connected; {attentionCount} job(s) need review" : printerIssue ? "Connected; exposed printers need attention" : "Connected; exposed printers ready");
         if(_updatePauseUntil > ObservationTime)
         {
            status = "Printing paused for application update. " + status;
         }
         (health, status) = ControlStatus(health, status, jobFailure);
         if(!_authorizationReady)
         {
            health = "RED";
            status = _authenticationMessage;
         }
         return new AgentSnapshot(configuration is not null, connected, health, status, configuration?.ServerUrl, configuration?.DeviceId, _lastServerContact?.ToString("O", System.Globalization.CultureInfo.InvariantCulture), printers, jobs, _log.Entries(), store.GetDeviceProfile(), _controlState, AuthenticationStatus(), GetTransportSnapshot(), PrinterSync: printerSync);
      }
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
            _log.Write("ERROR", "Local command failed: " + QqqSessionDiagnostics.Describe(exception));
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
   /// unknown operation is refused, and exposure changes validate their arguments before touching the store. While the
   /// stored state cannot be used, only the operations that need none of it are dispatched and every other one is refused
   /// with the reason.
   /// </summary>
   private async Task<string> HandleCommandAsync(PipeRequest request, CancellationToken token)
   {
      if(_storedState.Refusal is string refusal && !AnsweredWithoutStoredState(request.Operation))
      {
         throw new InvalidOperationException(refusal);
      }
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
            using(AgentStore store = OpenStore(recover: false))
            {
               store.SetExposure(request.PrinterId, request.Exposed.Value, _printers.Discover());
            }
            WakeClaims();
            _log.Write("INFO", request.Exposed.Value ? "A printer was shared with the server." : "A printer was hidden from the server.");
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



   /// <summary>
   /// The operations the service still answers while the stored state cannot be used: what it is doing and why (status,
   /// snapshot, and the transport read the companion repeats every second), the log operations, and the two update
   /// operations, without which the build that can use the state could not be installed. None of them opens a store.
   /// </summary>
   private static bool AnsweredWithoutStoredState(string operation)
      => operation is "status" or "snapshot" or "transport" or "get-log-settings" or "set-log-settings" or "clear-logs" or "prepare-update" or "resume-after-update";
}
