/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.Fakes;

/// <summary>Synthetic snapshots and a fixed time format shared by the presentation tests, so each test states only the fields it is about and no test depends on the machine's time zone or language.</summary>
internal static class TestData
{
   /// <summary>A time format fixed to two hours ahead of UTC and the invariant culture: "2026-11-10T10:00:00Z" reads "11/10/2026 12:00".</summary>
   internal static TimestampFormat Time { get; } = new(new FixedZoneTime(TimeSpan.FromHours(2)), CultureInfo.InvariantCulture);



   /// <summary>A printer with a Windows queue name and the given properties; by default a ready, unshared PDF printer that no server has been asked about.</summary>
   internal static PrinterSnapshot Printer(string id = "printer-1", string name = "Front Desk Laser", string status = "READY", string[]? formats = null, bool exposed = false,
      string? displayName = null, string? description = null, string? location = null, string serverState = PrinterServerState.NotChecked)
      => new(id, name, "Generic Driver", "USB001", status, formats ?? ["PDF"], exposed, displayName, description, location, ServerState: serverState);



   /// <summary>A job in the given state; by default a queued job with no title, printer or creation time recorded.</summary>
   internal static JobSnapshot Job(string id = "job-1", string status = "QUEUED", string? title = null, string? printerName = null, string? printerId = null,
      string? format = null, bool reviewed = false, string? createdAt = null, string? spoolerJobId = null, string? message = null)
      => new(id, status, "2026-11-10T10:05:00Z", message, spoolerJobId, printerId, printerName, title, format, reviewed, createdAt);



   /// <summary>A snapshot of an enrolled, connected service with the given printers and optional parts.</summary>
   internal static AgentSnapshot Snapshot(IReadOnlyList<PrinterSnapshot>? printers = null, bool enrolled = true, string health = "GREEN", PrinterServerSync? sync = null,
      AgentControlState? control = null, AgentAuthenticationStatus? authentication = null, string? serverUrl = "https://print.example.com", string? lastPoll = "2026-11-10T10:00:00Z")
      => new(enrolled, true, health, "Ready", serverUrl, "device-1", lastPoll, printers ?? [], [], [], Control: control, Authentication: authentication, PrinterSync: sync);
}
