using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Core.Transport;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

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

   private string Jobs()
   {
      JobQueries++;
      JobSnapshot[] jobs = SyntheticJobs(DateTime.UtcNow.ToString("O"));
      return JsonSerializer.Serialize(new JobPage(jobs, jobs.Length, 0, 50), PrintApiClient.JsonOptions);
   }

   private JobSnapshot[] SyntheticJobs(string now) => _changedSnapshot
      ? [new JobSnapshot("synthetic-new-job", "SPOOLED", now, "Synthetic completion during dialog", "synthetic-spool", Printer.PrinterId, Printer.Name, "Synthetic warehouse document completed while the dialog was open", "PDF", CreatedAt: now)]
      : [];

   internal void RecordSyntheticTraffic()
   {
      _traffic.RecordUpload(2048);
      _traffic.RecordDownload(4096);
   }

   private string ReadTransport()
   {
      TransportReads++;
      return JsonSerializer.Serialize(Transport(), PrintApiClient.JsonOptions);
   }

   private AgentTransportSnapshot Transport()
   {
      ApplicationTrafficSnapshot measured = _traffic.GetSnapshot();
      return new AgentTransportSnapshot("WSS", "CONNECTED", measured.UploadedBytes, measured.DownloadedBytes,
         measured.UploadBytesPerSecond, measured.DownloadBytesPerSecond, measured.TotalsSinceUtc);
   }

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
