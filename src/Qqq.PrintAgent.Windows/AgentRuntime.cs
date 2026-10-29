/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>Hosts outbound printing and the local administrator management pipe.</summary>
public sealed class AgentRuntime(string directory, Action<string> log, IPrinterBackend? printerBackend = null) : IDisposable
{
   private readonly Lock _stateLock = new();
   private readonly IPrinterBackend _printers = printerBackend ?? new WindowsPrinters();
   private readonly Queue<AgentLogEntry> _logs = new();
   private DateTimeOffset? _lastSuccessfulPoll;
   private string? _failure;

   /// <summary>No persistent database handles are held by the runtime.</summary>
   public void Dispose() { }



   /// <summary>Owns the data directory until Windows requests shutdown.</summary>
   public async Task RunAsync(CancellationToken token)
   {
      Directory.CreateDirectory(directory);
      using FileStream instance = new(Path.Combine(directory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
      WriteLog("INFO", "QQQ Print Agent service started.");
      await Task.WhenAll(PollAsync(token), ServePipeAsync(token)).ConfigureAwait(false);
   }



   private async Task PollAsync(CancellationToken token)
   {
      while(!token.IsCancellationRequested)
      {
         try
         {
            using AgentStore store = new(directory);
            AgentConfiguration? configuration = store.LoadConfiguration();
            if(configuration is not null)
            {
               using PrintApiClient api = new(configuration.ServerUrl, configuration.DeviceToken);
               ExposedPrinterBackend exposed = new(_printers, store.GetExposedPrinterIds);
               await new PrintWorker(api, exposed, store, message => WriteLog("INFO", message)).RunOnceAsync(token).ConfigureAwait(false);
               lock(_stateLock)
               {
                  _lastSuccessfulPoll = DateTimeOffset.UtcNow;
                  _failure = null;
               }
            }
         }
         catch(Exception exception) when(exception is not OperationCanceledException)
         {
            lock(_stateLock)
            {
               _failure = exception.Message;
            }
            WriteLog("ERROR", exception.Message);
         }
         await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
      }
   }



   /// <summary>Current local observations, with no token or document content.</summary>
   public AgentSnapshot GetSnapshot()
   {
      using AgentStore store = new(directory);
      AgentConfiguration? configuration = store.LoadConfiguration();
      IReadOnlySet<string> exposed = store.GetExposedPrinterIds();
      var printers = _printers.Discover().Select(printer => new PrinterSnapshot(printer.PrinterId, printer.Name, printer.DriverName, printer.PortName, printer.Status, printer.Formats, exposed.Contains(printer.PrinterId))).ToList();
      foreach(string missing in exposed.Where(id => !printers.Any(printer => string.Equals(printer.PrinterId, id, StringComparison.Ordinal))))
      {
         printers.Add(new PrinterSnapshot(missing, "Previously exposed printer is unavailable", "", "", "OFFLINE", [], true));
      }
      IReadOnlyList<JobSnapshot> jobs = store.RecentJobs();
      lock(_stateLock)
      {
         bool connected = configuration is not null && _failure is null && _lastSuccessfulPoll is not null && DateTimeOffset.UtcNow - _lastSuccessfulPoll.Value < TimeSpan.FromSeconds(30);
         bool printerIssue = !printers.Any(printer => printer.Exposed) || printers.Any(printer => printer.Exposed && !string.Equals(printer.Status, "READY", StringComparison.Ordinal));
         bool jobFailure = jobs.Count > 0 && jobs[0].Status is "FAILED" or "OUTCOME_UNKNOWN";
         string health = !connected || jobFailure ? "RED" : printerIssue ? "ORANGE" : "GREEN";
         string status = configuration is null ? "Not enrolled" : _failure ?? (!connected ? "Waiting for QQQ connection" : jobFailure ? "Connected; latest job needs attention" : printerIssue ? "Connected; exposed printers need attention" : "Connected; exposed printers ready");
         return new AgentSnapshot(configuration is not null, connected, health, status, configuration?.ServerUrl, configuration?.DeviceId, _lastSuccessfulPoll?.ToString("O", System.Globalization.CultureInfo.InvariantCulture), printers, jobs, _logs.ToArray());
      }
   }



   private void WriteLog(string level, string message)
   {
      string bounded = message.Length <= 1000 ? message : message[..1000];
      lock(_stateLock)
      {
         _logs.Enqueue(new AgentLogEntry(DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture), level, bounded));
         while(_logs.Count > 100)
         {
            _logs.Dequeue();
         }
      }
      log(bounded);
   }



   private async Task ServePipeAsync(CancellationToken token)
   {
      PipeSecurity security = new();
      security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null), PipeAccessRights.FullControl, AccessControlType.Allow));
      security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null), PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
      while(!token.IsCancellationRequested)
      {
         await using NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create("QqqPrintAgent", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
         await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
         using StreamReader reader = new(pipe, leaveOpen: true);
         await using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };
         try
         {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            string line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) ?? "";
            if(line.Length > 8192)
            {
               throw new InvalidDataException("Command too large.");
            }
            PipeRequest request = JsonSerializer.Deserialize<PipeRequest>(line, PrintApiClient.JsonOptions) ?? throw new InvalidDataException("Invalid command.");
            await writer.WriteLineAsync(await HandleCommandAsync(request, timeout.Token).ConfigureAwait(false)).ConfigureAwait(false);
         }
         catch(Exception exception) when(exception is not OperationCanceledException || !token.IsCancellationRequested)
         {
            await writer.WriteLineAsync("Error: " + exception.Message).ConfigureAwait(false);
         }
      }
   }



   private async Task<string> HandleCommandAsync(PipeRequest request, CancellationToken token)
   {
      switch(request.Operation)
      {
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
            WriteLog("INFO", request.Exposed.Value ? "A printer was exposed to QQQ." : "A printer was hidden from QQQ.");
            return JsonSerializer.Serialize(GetSnapshot(), PrintApiClient.JsonOptions);
         case "enroll":
            return await EnrollAsync(request, token).ConfigureAwait(false);
         default:
            throw new InvalidOperationException("Unknown command.");
      }
   }



   private async Task<string> EnrollAsync(PipeRequest request, CancellationToken token)
   {
      using PrintApiClient api = new(request.ServerUrl ?? throw new InvalidOperationException("Server URL required."));
      EnrollmentResponse response = await api.EnrollAsync(new EnrollmentRequest(request.EnrollmentCode ?? "", Environment.MachineName, "0.1.0"), token).ConfigureAwait(false);
      using AgentStore store = new(directory);
      store.SaveConfiguration(new AgentConfiguration(request.ServerUrl!, response.DeviceId, response.DeviceToken, response.PollIntervalSeconds));
      lock(_stateLock)
      {
         _lastSuccessfulPoll = null;
         _failure = null;
      }
      WriteLog("INFO", "Device enrolled with QQQ.");
      return $"Enrolled device {response.DeviceId}";
   }
}

