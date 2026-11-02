/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Core.Transport;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

/// <summary>
/// A non-printing stand-in for the Windows service behind the real window. It answers only the four read and test-print pipe
/// operations the layout run needs with fixed synthetic data, counts how often each was used so the scenario can prove the UI
/// really refreshed, and rejects any other operation so nothing unexpected can reach a printer.
/// </summary>
internal sealed class SyntheticService
{
   internal static PrinterSnapshot Printer { get; } = new("synthetic-zebra", "Synthetic Zebra-2", "ZDesigner ZT411-300dpi ZPL", "synthetic", "READY", ["PDF", "ZPL"], true, DisplayName: "Z2");
   internal int Submissions { get; private set; }
   internal int Snapshots { get; private set; }
   internal int JobQueries { get; private set; }
   internal int DialogSnapshots { get; private set; }
   internal int TransportReads { get; private set; }
   private readonly ApplicationTrafficMeter _traffic = new();
   internal bool DialogOpen { get; set; }
   private bool _changedSnapshot;
   internal const string ChangedDescription = "Synthetic inventory changed while the test dialog was open. This longer description changes the selected row's wrapping and available list viewport without printing.";



   /// <summary>Answers one pipe request after a short delay that mimics real service latency; any operation outside the allowed four throws.</summary>
   internal async Task<string> SendAsync(PipeRequest request)
   {
      await Task.Delay(30);
      return request.Operation switch
      {
         "snapshot" => Snapshot(),
         "query-jobs" => Jobs(),
         "transport" => ReadTransport(),
         "test-print" => Submit(request),
         _ => throw new InvalidOperationException("The nonprinting fixture rejected an unexpected operation: " + request.Operation),
      };
   }



   /// <summary>
   /// Builds the agent snapshot: four printers (the Zebra one the scenario selects, whose description and location change once a
   /// snapshot is taken while the dialog is open), connected and signed-in status, and transport figures. The changed snapshot
   /// is how the test proves the timer refreshed the screen behind an open dialog.
   /// </summary>
   private string Snapshot()
   {
      Snapshots++;
      if(DialogOpen)
      {
         DialogSnapshots++;
         _changedSnapshot = true;
      }
      string now = DateTime.UtcNow.ToString("O");
      PrinterSnapshot[] printers =
      [
         new("synthetic-laser", "Synthetic Laser", "Generic laser", "synthetic", "READY", ["PDF"], true),
         new("synthetic-pdf", "Microsoft Print to PDF", "Microsoft Print To PDF", "synthetic", "READY", [], false),
         new("synthetic-note", "Synthetic Notes Printer", "Generic interactive", "synthetic", "READY", [], false),
         _changedSnapshot ? Printer with { Description = ChangedDescription, Location = "Synthetic packing station, updated during dialog" } : Printer,
      ];
      var snapshot = new AgentSnapshot(true, true, "GREEN", "Connected to synthetic server", "https://synthetic.invalid", "synthetic-device", now, printers, SyntheticJobs(now), [],
         Control: new AgentControlState("SUPPORTED", new DeviceDesiredState(4, false, "CONNECTED"), LastContact: now,
            LastCommand: _changedSnapshot ? "REFRESH_INVENTORY" : null, LastOutcome: _changedSnapshot ? "INVENTORY_REFRESHED" : null, LastCommandAt: _changedSnapshot ? now : null),
         Authentication: new AgentAuthenticationStatus(true, "synthetic-user", "synthetic-tenant", "Signed in", true), Transport: Transport());
      return JsonSerializer.Serialize(snapshot, PrintApiClient.JsonOptions);
   }



   /// <summary>Returns a one-page job list and counts the query, so the scenario can confirm the Jobs screen asked the service.</summary>
   private string Jobs()
   {
      JobQueries++;
      JobSnapshot[] jobs = SyntheticJobs(DateTime.UtcNow.ToString("O"));
      return JsonSerializer.Serialize(new JobPage(jobs, jobs.Length, 0, 50), PrintApiClient.JsonOptions);
   }



   /// <summary>No jobs until the changed snapshot, then one completed job that appears while the dialog is open.</summary>
   private JobSnapshot[] SyntheticJobs(string now) => _changedSnapshot
      ? [new JobSnapshot("synthetic-new-job", "SPOOLED", now, "Synthetic completion during dialog", "synthetic-spool", Printer.PrinterId, Printer.Name, "Synthetic warehouse document completed while the dialog was open", "PDF", CreatedAt: now)]
      : [];



   /// <summary>Feeds the real traffic meter 2 KiB up and 4 KiB down so the footer's rate and totals can be checked against known numbers.</summary>
   internal void RecordSyntheticTraffic()
   {
      _traffic.RecordUpload(2048);
      _traffic.RecordDownload(4096);
   }



   /// <summary>Returns the transport snapshot as the service would over the pipe and counts the read.</summary>
   private string ReadTransport()
   {
      TransportReads++;
      return JsonSerializer.Serialize(Transport(), PrintApiClient.JsonOptions);
   }



   /// <summary>Reports a connected secure WebSocket with the byte counts and rates the traffic meter has measured.</summary>
   private AgentTransportSnapshot Transport()
   {
      ApplicationTrafficSnapshot measured = _traffic.GetSnapshot();
      return new AgentTransportSnapshot("WSS", "CONNECTED", measured.UploadedBytes, measured.DownloadedBytes,
         measured.UploadBytesPerSecond, measured.DownloadBytesPerSecond, measured.TotalsSinceUtc);
   }



   /// <summary>
   /// Accepts only a ZPL test for the synthetic Zebra queue and merely counts it. Nothing is queued anywhere, so the run proves
   /// the dialog's request while guaranteeing no document is created or printed.
   /// </summary>
   private string Submit(PipeRequest request)
   {
      if(!string.Equals(request.PrinterId, Printer.PrinterId, StringComparison.Ordinal) || !string.Equals(request.Format, "ZPL", StringComparison.Ordinal))
      {
         throw new InvalidOperationException("Unexpected synthetic test selection.");
      }
      Submissions++;
      return "Queued on the server. The new job will appear here when this device receives it.";
   }
}
