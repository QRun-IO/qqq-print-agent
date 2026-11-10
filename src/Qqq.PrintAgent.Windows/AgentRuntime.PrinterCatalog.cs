/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Printer approval part of the runtime. On a printing 2.4 server that negotiated printer-approval-v1 the server pushes a
/// PRINTERS frame after each accepted inventory and whenever an administrator changes an approval, and that frame is the
/// authoritative reading. On an older server the service reads the operator printer catalog after the inventory is accepted and
/// about once a minute. Either way the result is kept in memory only, reset with the connection, and shown per printer in the
/// status snapshot so the companion can say "approved", "awaiting approval" or "not reported" for the connected server.
/// </summary>
public sealed partial class AgentRuntime
{
   /// <summary>How long a catalog reading stays current before the synchronization loop reads it again, measured on the runtime's time provider.</summary>
   internal static readonly TimeSpan CatalogRefreshInterval = TimeSpan.FromSeconds(60);

   private HashSet<string>? _catalogApproved;
   private PrinterServerSync _catalogSync = PrinterServerSync.Initial;
   private DateTimeOffset? _catalogCheckedAt;



   /// <summary>True while the current connection negotiated printer-approval-v1, so the server pushes approval and the catalog is not read.</summary>
   private bool ApprovalNegotiated
   {
      get
      {
         lock(_stateLock)
         { return _approvalNegotiated; }
      }
   }



   /// <summary>
   /// Applies the server's PRINTERS frame: every printer in the latest accepted inventory with its approval. The enabled ones
   /// become the approved set; a shared printer the frame lists as disabled, or does not list yet, is awaiting approval.
   /// </summary>
   private void ApplyPrinterApproval(RealtimeEnvelope envelope)
   {
      PrinterApprovalState[] printers = envelope.Payload.TryGetProperty("printers", out JsonElement list) && list.ValueKind == JsonValueKind.Array
         ? list.Deserialize<PrinterApprovalState[]>(PrintApiClient.JsonOptions) ?? [] : throw new InvalidDataException("The PRINTERS frame has no printers list.");
      if(printers.Any(printer => string.IsNullOrEmpty(printer?.PrinterId)))
      { throw new InvalidDataException("The PRINTERS frame names a printer without an identifier."); }
      var approved = printers.Where(printer => printer.Enabled).Select(printer => printer.PrinterId).ToHashSet(StringComparer.Ordinal);
      string server = _transportOrigin?.Authority ?? "the server";
      RecordCatalog(approved, new PrinterServerSync(PrinterServerSync.Reported, Now(), null, server, PrinterServerSync.RealtimeSource),
         printers.Select(printer => printer.PrinterId).ToArray(), id => id, printers.Length);
   }



   /// <summary>Forgets the previous server's catalog reading, so a new connection or server starts from "not checked".</summary>
   private void ResetPrinterCatalog()
   {
      lock(_stateLock)
      {
         _catalogApproved = null;
         _catalogSync = PrinterServerSync.Initial;
         _catalogCheckedAt = null;
      }
   }



   /// <summary>The server-side state of one printer given the local sharing choice and the last catalog reading.</summary>
   private string ServerStateFor(string printerId, bool exposed)
   {
      if(!exposed)
      { return PrinterServerState.NotShared; }
      lock(_stateLock)
      {
         return _catalogSync.Outcome switch
         {
            PrinterServerSync.Reported => _catalogApproved?.Contains(printerId) == true ? PrinterServerState.Approved : PrinterServerState.AwaitingApproval,
            PrinterServerSync.NotReported => PrinterServerState.NotReported,
            _ => PrinterServerState.NotChecked,
         };
      }
   }



   /// <summary>
   /// Reads the catalog unless a reading is still current (or <paramref name="force"/> is set, after a fresh inventory), keeps
   /// the printers the server lists for this device, and logs the outcome whenever it changes: the approved set at INFO, a
   /// catalog that cannot be read at WARN with the reason, once rather than every minute. A failure never breaks the connection.
   /// </summary>
   private async Task RefreshPrinterCatalogAsync(PrintApiClient api, AgentConfiguration configuration, IReadOnlyList<PrinterInfo> shared, bool force, CancellationToken token)
   {
      lock(_stateLock)
      {
         if(!force && _catalogCheckedAt is DateTimeOffset checkedAt && ObservationTime - checkedAt < CatalogRefreshInterval)
         { return; }
      }
      string server = new Uri(configuration.ServerUrl).Authority;
      try
      {
         IReadOnlyList<OperatorPrinter> catalog = await api.OperatorPrintersAsync(token).ConfigureAwait(false);
         var approved = catalog.Where(printer => string.Equals(printer.DeviceId, configuration.DeviceId, StringComparison.Ordinal))
            .Select(printer => printer.PrinterId).ToHashSet(StringComparer.Ordinal);
         RecordCatalog(approved, new PrinterServerSync(PrinterServerSync.Reported, Now(), null, server, PrinterServerSync.CatalogSource), shared.Select(printer => printer.PrinterId).ToArray(), NameOf(shared), catalog.Count);
      }
      catch(OperationCanceledException) when(token.IsCancellationRequested)
      { throw; }
      catch(Exception exception) when(exception is NotSupportedException or UnauthorizedAccessException or HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException or ArgumentException or InvalidOperationException or QqqSessionException)
      {
         string reason = exception switch
         {
            NotSupportedException or UnauthorizedAccessException => exception.Message,
            QqqSessionException { Failure: QqqSessionException.FailureKind.PermissionDenied } => "The signed-in user may not read the operator printer catalog (HTTP 403 Forbidden).",
            _ => QqqSessionDiagnostics.Explain(exception),
         };
         RecordCatalog(null, new PrinterServerSync(PrinterServerSync.NotReported, Now(), reason, server, PrinterServerSync.CatalogSource), shared.Select(printer => printer.PrinterId).ToArray(), NameOf(shared), 0);
         _log.Write("DEBUG", "Printer catalog detail: " + exception);
      }
   }



   /// <summary>Maps a shared printer's identifier to its Windows queue name for the log, falling back to the identifier.</summary>
   private static Func<string, string> NameOf(IReadOnlyList<PrinterInfo> shared)
      => id => shared.FirstOrDefault(printer => string.Equals(printer.PrinterId, id, StringComparison.Ordinal))?.Name ?? id;



   /// <summary>Stores a reading and logs it when the approved set or the outcome differs from the previous reading.</summary>
   private void RecordCatalog(HashSet<string>? approved, PrinterServerSync sync, string[] shared, Func<string, string> nameOf, int catalogCount)
   {
      bool changed;
      lock(_stateLock)
      {
         changed = !string.Equals(_catalogSync.Outcome, sync.Outcome, StringComparison.Ordinal) || !string.Equals(_catalogSync.Detail, sync.Detail, StringComparison.Ordinal)
            || (approved is not null && (_catalogApproved is null || !_catalogApproved.SetEquals(approved)));
         _catalogApproved = approved;
         _catalogSync = sync;
         _catalogCheckedAt = ObservationTime;
      }
      if(!changed)
      { return; }
      if(approved is null)
      {
         _log.Write("WARN", $"Printer approval is not reported by {sync.Server}: {sync.Detail} Shared printers show as 'not reported' until the catalog can be read.");
         return;
      }
      string[] approvedNames = shared.Where(approved.Contains).Select(nameOf).ToArray();
      string[] waitingNames = shared.Where(id => !approved.Contains(id)).Select(nameOf).ToArray();
      string source = string.Equals(sync.Source, PrinterServerSync.RealtimeSource, StringComparison.Ordinal) ? "Printer approval pushed by" : "Printer catalog from";
      _log.Write("INFO", $"{source} {sync.Server}: {approvedNames.Length} of {shared.Length} shared printer(s) approved for this device"
         + (approvedNames.Length > 0 ? $" [{string.Join(", ", approvedNames)}]" : "")
         + (waitingNames.Length > 0 ? $"; awaiting approval on the server [{string.Join(", ", waitingNames)}]" : "")
         + (string.Equals(sync.Source, PrinterServerSync.RealtimeSource, StringComparison.Ordinal) ? "." : $"; the catalog listed {catalogCount} destination(s) in total."));
   }
}
