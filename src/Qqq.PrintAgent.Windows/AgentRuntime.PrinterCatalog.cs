/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Printer catalog part of the runtime: after the server accepts the inventory, and then about once a minute while connected,
/// the service reads the server's operator printer catalog to learn which shared printers the server has approved. The result
/// is kept in memory only, reset with the connection, and shown per printer in the status snapshot so the companion can say
/// "approved", "awaiting approval" or "not reported" for the server it is connected to.
/// </summary>
public sealed partial class AgentRuntime
{
   /// <summary>How long a catalog reading stays current before the synchronization loop reads it again.</summary>
   internal static readonly TimeSpan CatalogRefreshInterval = TimeSpan.FromSeconds(60);

   private HashSet<string>? _catalogApproved;
   private PrinterServerSync _catalogSync = PrinterServerSync.Initial;
   private DateTimeOffset? _catalogCheckedAt;



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
         if(!force && _catalogCheckedAt is DateTimeOffset checkedAt && DateTimeOffset.UtcNow - checkedAt < CatalogRefreshInterval)
         { return; }
      }
      string server = new Uri(configuration.ServerUrl).Authority;
      try
      {
         IReadOnlyList<OperatorPrinter> catalog = await api.OperatorPrintersAsync(token).ConfigureAwait(false);
         var approved = catalog.Where(printer => string.Equals(printer.DeviceId, configuration.DeviceId, StringComparison.Ordinal))
            .Select(printer => printer.PrinterId).ToHashSet(StringComparer.Ordinal);
         RecordCatalog(approved, new PrinterServerSync(PrinterServerSync.Reported, Now(), null, server), shared, catalog.Count);
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
         RecordCatalog(null, new PrinterServerSync(PrinterServerSync.NotReported, Now(), reason, server), shared, 0);
         WriteLog("DEBUG", "Printer catalog detail: " + exception);
      }
   }



   /// <summary>Stores a reading and logs it when the approved set or the outcome differs from the previous reading.</summary>
   private void RecordCatalog(HashSet<string>? approved, PrinterServerSync sync, IReadOnlyList<PrinterInfo> shared, int catalogCount)
   {
      bool changed;
      lock(_stateLock)
      {
         changed = !string.Equals(_catalogSync.Outcome, sync.Outcome, StringComparison.Ordinal) || !string.Equals(_catalogSync.Detail, sync.Detail, StringComparison.Ordinal)
            || (approved is not null && (_catalogApproved is null || !_catalogApproved.SetEquals(approved)));
         _catalogApproved = approved;
         _catalogSync = sync;
         _catalogCheckedAt = DateTimeOffset.UtcNow;
      }
      if(!changed)
      { return; }
      if(approved is null)
      {
         WriteLog("WARN", $"Printer approval is not reported by {sync.Server}: {sync.Detail} Shared printers show as 'not reported' until the catalog can be read.");
         return;
      }
      string[] approvedNames = shared.Where(printer => approved.Contains(printer.PrinterId)).Select(printer => printer.Name).ToArray();
      string[] waitingNames = shared.Where(printer => !approved.Contains(printer.PrinterId)).Select(printer => printer.Name).ToArray();
      WriteLog("INFO", $"Printer catalog from {sync.Server}: {approvedNames.Length} of {shared.Count} shared printer(s) approved for this device"
         + (approvedNames.Length > 0 ? $" [{string.Join(", ", approvedNames)}]" : "")
         + (waitingNames.Length > 0 ? $"; awaiting approval on the server [{string.Join(", ", waitingNames)}]" : "")
         + $"; the catalog listed {catalogCount} destination(s) in total.");
   }
}
