/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using KofTwentyTwo.AppKit.Logging;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>Hosts outbound printing and the local administrator management pipe.</summary>
public sealed partial class AgentRuntime(string directory, Action<string> log, IPrinterBackend? printerBackend = null,
   Func<AgentConfiguration, PrintApiClient>? apiFactory = null, string pipeName = "QqqPrintAgent", Func<NamedPipeServerStream>? pipeFactory = null) : IDisposable
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

   /// <summary>No persistent database handles are held by the runtime.</summary>
   public void Dispose()
   {
      _pollRequested.Dispose();
      _connectionGate.Dispose();
   }



   /// <summary>Owns the data directory until Windows requests shutdown.</summary>
   public async Task RunAsync(CancellationToken token)
   {
      Directory.CreateDirectory(directory);
      using FileStream instance = new(Path.Combine(directory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
      WriteLog("INFO", "Print service started.");
      using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
      Task poll = PollAsync(lifetime.Token);
      Task pipe = ServePipeAsync(lifetime.Token);
      Task heartbeat = HeartbeatAsync(lifetime.Token);
      await Task.WhenAny(poll, pipe, heartbeat).ConfigureAwait(false);
      await lifetime.CancelAsync().ConfigureAwait(false);
      await Task.WhenAll(poll, pipe, heartbeat).ConfigureAwait(false);
   }



   private async Task PollAsync(CancellationToken token)
   {
      while(!token.IsCancellationRequested)
      {
         try
         {
            await _connectionGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
               await PollOnceAsync(token).ConfigureAwait(false);
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
         await _pollRequested.WaitAsync(TimeSpan.FromSeconds(_pollIntervalSeconds), token).ConfigureAwait(false);
      }
   }

   private async Task PollOnceAsync(CancellationToken token)
   {
      if(UpdatePaused)
      {
         return;
      }
      using AgentStore store = new(directory);
      AgentConfiguration? configuration = store.LoadConfiguration();
      if(configuration is null)
      { return; }
      _pollIntervalSeconds = Math.Clamp(configuration.PollIntervalSeconds, 1, 20);
      using PrintApiClient api = CreateApi(configuration);
      long queueGeneration;
      lock(_stateLock)
      {
         queueGeneration = _queueGeneration;
      }
      IReadOnlyList<PrintJob> queued = await api.QueuedJobsAsync(token).ConfigureAwait(false);
      lock(_stateLock)
      {
         if(queueGeneration == _queueGeneration)
         {
            _queuedJobs = queued.Select(job => new JobSnapshot(job.JobId, "QUEUED", job.CreatedAt, "Waiting on the server; not yet received by this device.", null, job.PrinterId, _queuedJobs.FirstOrDefault(previous => string.Equals(previous.PrinterId, job.PrinterId, StringComparison.Ordinal))?.PrinterName, job.Title, job.Format, CreatedAt: JobSorting.NormalizeCreatedAt(job.CreatedAt))).ToArray();
         }
      }
      ExposedPrinterBackend exposed = new(new ProfiledPrinterBackend(_printers, store), store.GetExposedPrinterIds);
      await new PrintWorker(api, exposed, store, message => WriteLog("INFO", message)).RunOnceAsync(token).ConfigureAwait(false);
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
   }

   private PrintApiClient CreateApi(AgentConfiguration configuration) => apiFactory?.Invoke(configuration) ?? new PrintApiClient(configuration.ServerUrl, configuration.DeviceToken);

   private void WakePoll()
   {
      lock(_stateLock)
      {
         if(_pollRequested.CurrentCount == 0)
         { _pollRequested.Release(); }
      }
   }



   /// <summary>Current local observations, with no token or document content.</summary>
   public AgentSnapshot GetSnapshot()
   {
      using AgentStore store = new(directory);
      AgentConfiguration? configuration = store.LoadConfiguration();
      IReadOnlySet<string> exposed = store.GetExposedPrinterIds();
      var printers = new ProfiledPrinterBackend(_printers, store).Discover().Select(printer => new PrinterSnapshot(printer.PrinterId, printer.Name, printer.DriverName, printer.PortName, printer.Status, printer.Formats, exposed.Contains(printer.PrinterId), printer.DisplayName, printer.Description, printer.Location)).ToList();
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
         bool connected = configuration is not null && _failure is null && _heartbeatFailure is null && _lastSuccessfulPoll is not null && DateTimeOffset.UtcNow - _lastSuccessfulPoll.Value < TimeSpan.FromSeconds(30);
         bool printerIssue = !printers.Any(printer => printer.Exposed) || printers.Any(printer => printer.Exposed && !string.Equals(printer.Status, "READY", StringComparison.Ordinal));
         bool jobFailure = attentionCount > 0;
         string health = !connected || jobFailure ? "RED" : printerIssue ? "ORANGE" : "GREEN";
         string status = configuration is null ? "Not enrolled" : _failure ?? _heartbeatFailure ?? (!connected ? "Waiting for server connection" : jobFailure ? $"Connected; {attentionCount} job(s) need review" : printerIssue ? "Connected; exposed printers need attention" : "Connected; exposed printers ready");
         if(_updatePauseUntil > DateTimeOffset.UtcNow)
         {
            status = "Printing paused for application update. " + status;
         }
         return new AgentSnapshot(configuration is not null, connected, health, status, configuration?.ServerUrl, configuration?.DeviceId, _lastSuccessfulPoll?.ToString("O", System.Globalization.CultureInfo.InvariantCulture), printers, jobs, _logs.ToArray(), store.GetDeviceProfile());
      }
   }



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



   private async Task ServePipeAsync(CancellationToken token)
   {
      PipeSecurity security = new();
      security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null), PipeAccessRights.FullControl, AccessControlType.Allow));
      security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null), PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
      while(!token.IsCancellationRequested)
      {
         await using NamedPipeServerStream pipe = pipeFactory?.Invoke() ?? NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
         await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
         using StreamReader reader = new(pipe, leaveOpen: true);
         StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };
         try
         {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            string line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) ?? "";
            if(line.Length > 65536)
            {
               throw new InvalidDataException("Command too large.");
            }
            PipeRequest request = JsonSerializer.Deserialize<PipeRequest>(line, PrintApiClient.JsonOptions) ?? throw new InvalidDataException("Invalid command.");
            timeout.CancelAfter(TimeSpan.FromSeconds(string.Equals(request.Operation, "enroll", StringComparison.Ordinal) ? 120 : 30));
            await writer.WriteLineAsync(await HandleCommandAsync(request, timeout.Token).ConfigureAwait(false)).ConfigureAwait(false);
         }
         catch(Exception exception) when(exception is not OperationCanceledException || !token.IsCancellationRequested)
         {
            WriteLog("ERROR", "Local command failed: " + exception.Message);
            string message = exception is System.Security.Cryptography.CryptographicException
               ? "The saved connection cannot be opened. Open Settings, choose Connect this device or Sign in again, and use a fresh server enrollment code. Existing job history will be preserved."
               : AgentActivityLog.Redact(exception.Message);
            try
            { await writer.WriteLineAsync("Error: " + message).ConfigureAwait(false); }
            catch(IOException) { /* A disconnected client must not stop the management pipe. */ }
         }
         finally
         {
            try
            { await writer.DisposeAsync().ConfigureAwait(false); }
            catch(IOException) { /* Failed response buffers cannot be flushed to a closed pipe. */ }
         }
      }
   }



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
            await _connectionGate.WaitAsync(token).ConfigureAwait(false);
            try
            { return await EnrollAsync(request, token).ConfigureAwait(false); }
            finally { _connectionGate.Release(); }
         case "reconnect":
            using(AgentStore store = new(directory))
            {
               if(store.LoadConfiguration() is null)
               { return "Not enrolled. Open Connection settings to connect this device."; }
            }
            WakePoll();
            WriteLog("INFO", "Connection retry requested.");
            return "Reconnect requested. Connection status will update after the next server response.";
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



   private async Task<string> EnrollAsync(PipeRequest request, CancellationToken token)
   {
      using PrintApiClient api = new(request.ServerUrl ?? throw new InvalidOperationException("Server URL required."));
      EnrollmentResponse response = await api.EnrollAsync(new EnrollmentRequest(request.EnrollmentCode ?? "", Environment.MachineName, PrintApiClient.AgentVersion), token).ConfigureAwait(false);
      using AgentStore store = new(directory, recoverUnreadableConfiguration: true);
      store.SaveConfiguration(new AgentConfiguration(request.ServerUrl!, response.DeviceId, response.DeviceToken, response.PollIntervalSeconds));
      ResetConnectionState();
      lock(_stateLock)
      {
         _queuedJobs = [];
      }
      WriteLog("INFO", "Device enrolled with the print server.");
      WakePoll();
      return $"Enrolled device {response.DeviceId}";
   }
}

