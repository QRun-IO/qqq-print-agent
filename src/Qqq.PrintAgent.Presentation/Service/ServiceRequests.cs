/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Service;

/// <summary>
/// The state-changing requests an operator can make, each built in one place with exactly the fields the service reads.
/// They are requests rather than client methods because the window runs them all the same way: confirm, send, show the
/// result by operation name, refresh.
/// </summary>
public static class ServiceRequests
{
   /// <summary>Prints another copy as a new job. The duplicate-risk flag records that the operator was warned and confirmed.</summary>
   public static PipeRequest RetryJob(string jobId) => new("retry-job", JobId: jobId, ConfirmDuplicateRisk: true);



   /// <summary>Cancels a queued job, or asks Windows to remove a spooled one.</summary>
   public static PipeRequest CancelJob(string jobId) => new("cancel-job", JobId: jobId);



   /// <summary>Records the operator's review of a failed or uncertain job without changing its outcome.</summary>
   public static PipeRequest AcknowledgeJob(string jobId) => new("acknowledge-job", JobId: jobId);



   /// <summary>Asks the server for one diagnostic document in the chosen format and stock profile for one printer.</summary>
   public static PipeRequest TestPrint(string printerId, string format, string profile) => new("test-print", PrinterId: printerId, Format: format, TestProfile: profile);



   /// <summary>Saves this computer's own name, description and location.</summary>
   public static PipeRequest SetDeviceProfile(string name, string description, string location) => new("set-device-profile", DeviceProfile: new DeviceProfile(name, description, location));



   /// <summary>Saves one printer's friendly name, description and location; the Windows queue is not renamed.</summary>
   public static PipeRequest SetPrinterProfile(string printerId, string name, string description, string location)
      => new("set-printer-profile", PrinterId: printerId, PrinterProfile: new PrinterProfile(name, description, location));



   /// <summary>Saves the shared diagnostic log limits.</summary>
   public static PipeRequest SetLogSettings(LogSettings settings) => new("set-log-settings", LogSettings: settings);



   /// <summary>Deletes the retained diagnostic logs; job history and settings are kept by the service.</summary>
   public static PipeRequest ClearLogs() => new("clear-logs");
}
