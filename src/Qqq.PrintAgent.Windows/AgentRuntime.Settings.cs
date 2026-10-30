/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

public sealed partial class AgentRuntime
{
   private string HandleSettingsCommand(PipeRequest request)
   {
      switch(request.Operation)
      {
         case "get-log-settings":
            return JsonSerializer.Serialize(AgentActivityLog.GetSettings(directory), PrintApiClient.JsonOptions);
         case "set-log-settings":
            AgentActivityLog.SaveSettings(directory, request.LogSettings ?? throw new InvalidOperationException("Log settings are required."));
            return JsonSerializer.Serialize(AgentActivityLog.GetSettings(directory), PrintApiClient.JsonOptions);
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
