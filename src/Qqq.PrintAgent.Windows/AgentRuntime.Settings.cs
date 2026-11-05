/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Settings commands of the runtime: log options and device and printer profiles. They live in one place so every change is followed by
/// a poll wake-up and a fresh snapshot, and the caller sees the state that was actually stored.
/// </summary>
public sealed partial class AgentRuntime
{
   /// <summary>Runs one log or profile command and returns its JSON result. Each store use is scoped to one command so a failed save leaves no open database, and unknown or incomplete requests are rejected instead of ignored.</summary>
   private string HandleSettingsCommand(PipeRequest request)
   {
      switch(request.Operation)
      {
         case "get-log-settings":
            return JsonSerializer.Serialize(AgentActivityLog.GetSettings(directory), PrintApiClient.JsonOptions);
         case "set-log-settings":
            AgentActivityLog.SaveSettings(directory, request.LogSettings ?? throw new InvalidOperationException("Log settings are required."));
            return JsonSerializer.Serialize(AgentActivityLog.GetSettings(directory), PrintApiClient.JsonOptions);
         case "get-server-trust":
            using(var store = new AgentStore(directory, recoverUnreadableConfiguration: true))
            {
               return JsonSerializer.Serialize(store.LoadServerTrust(), PrintApiClient.JsonOptions);
            }
         case "set-server-trust":
            ServerTrust trust = request.ServerTrust ?? throw new InvalidOperationException("Server trust settings are required.");
            if(trust.AllowUntrustedCertificate && (trust.Origin is null || !Uri.TryCreate(trust.Origin, UriKind.Absolute, out Uri? trusted) || !string.Equals(ServerTrust.Normalize(trusted), trust.Origin, StringComparison.Ordinal)))
            {
               throw new InvalidOperationException("Enter the server address before allowing its untrusted certificate; the exception is saved for that one server only.");
            }
            trust = trust.AllowUntrustedCertificate ? trust : new ServerTrust();
            using(var store = new AgentStore(directory, recoverUnreadableConfiguration: true))
            {
               store.SaveServerTrust(trust);
            }
            WriteLog(trust.AllowUntrustedCertificate ? "WARN" : "INFO", trust.AllowUntrustedCertificate
               ? $"An administrator ALLOWED untrusted server certificates for {trust.Origin}. The service and companion will accept any certificate, including a forged one, from that server only; use this with a development server and turn it off afterwards."
               : "Untrusted server certificates are refused again for every server (default). Only certificates Windows trusts for the LocalSystem account are accepted.");
            return JsonSerializer.Serialize(trust, PrintApiClient.JsonOptions);
         case "clear-logs":
            int deleted = AgentActivityLog.ClearLogs(directory);
            lock(_stateLock)
            {
               _logs.Clear();
            }
            WriteLog("INFO", "Activity logs cleared by an administrator. Job history and enrollment were retained.");
            return $"Cleared {deleted} activity log files. Job history and enrollment were retained.";
         case "get-device-profile":
            using(var store = new AgentStore(directory))
            {
               return JsonSerializer.Serialize(store.GetDeviceProfile(), PrintApiClient.JsonOptions);
            }
         case "set-device-profile":
            using(var store = new AgentStore(directory))
            {
               store.SaveDeviceProfile(request.DeviceProfile ?? throw new InvalidOperationException("Device profile is required."));
            }
            break;
         case "set-printer-profile":
            using(var store = new AgentStore(directory))
            {
               store.SavePrinterProfile(request.PrinterId ?? throw new InvalidOperationException("Select a printer."), request.PrinterProfile ?? throw new InvalidOperationException("Printer profile is required."));
            }
            break;
         default:
            throw new InvalidOperationException("Unknown settings command.");
      }
      WakePoll();
      return JsonSerializer.Serialize(GetSnapshot(), PrintApiClient.JsonOptions);
   }
}
