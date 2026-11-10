/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The server, not the local sharing choice, decides whether a shared printer may print. These tests prove the runtime asks the
/// connected server's catalog after the inventory is accepted, shows each shared printer as approved or awaiting approval for
/// that server, says "not reported" (with the reason, logged once) when the catalog cannot be read, and starts over on reconnect.
/// </summary>
public sealed class AgentPrinterCatalogTests
{
   /// <summary>After the inventory is accepted the catalog is read; the printer the server lists is approved, the other shared one is awaiting approval, and the summary names the server.</summary>
   [Fact]
   public async Task SharedPrinters_ShowApprovalFromTheConnectedServersCatalog()
   {
      await using RealtimeRuntimeFixture fixture = new();
      fixture.ApprovedPrinters = ["printer"];
      await using RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => string.Equals(fixture.Runtime.GetSnapshot().PrinterSync?.Outcome, PrinterServerSync.Reported, StringComparison.Ordinal));
      AgentSnapshot snapshot = fixture.Runtime.GetSnapshot();
      Assert.Equal(PrinterServerState.Approved, snapshot.Printers.Single(printer => string.Equals(printer.PrinterId, "printer", StringComparison.Ordinal)).ServerState);
      Assert.Equal(PrinterServerState.AwaitingApproval, snapshot.Printers.Single(printer => string.Equals(printer.PrinterId, "other", StringComparison.Ordinal)).ServerState);
      Assert.Equal(fixture.Peer.Origin.Authority, snapshot.PrinterSync!.Server);
      Assert.Contains(fixture.Logs, line => line.Contains("1 of 2 shared printer(s) approved", StringComparison.Ordinal) && line.Contains("awaiting approval on the server [Other synthetic]", StringComparison.Ordinal));
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>A printer that is not shared is reported as such regardless of the catalog, and never counted as awaiting approval.</summary>
   [Fact]
   public async Task UnsharedPrinter_IsNotShared_NotAwaitingApproval()
   {
      await using RealtimeRuntimeFixture fixture = new();
      using(AgentStore store = new(fixture.DirectoryPath))
      { store.SetExposure("other", false, fixture.Printer.Discover()); }
      await using RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => string.Equals(fixture.Runtime.GetSnapshot().PrinterSync?.Outcome, PrinterServerSync.Reported, StringComparison.Ordinal));
      AgentSnapshot snapshot = fixture.Runtime.GetSnapshot();
      Assert.Equal(PrinterServerState.NotShared, snapshot.Printers.Single(printer => string.Equals(printer.PrinterId, "other", StringComparison.Ordinal)).ServerState);
      Assert.Equal(PrinterServerState.Approved, snapshot.Printers.Single(printer => string.Equals(printer.PrinterId, "printer", StringComparison.Ordinal)).ServerState);
   }



   /// <summary>When the catalog cannot be read the shared printers are "not reported" with the server's reason, logged at WARN once, and the connection keeps working.</summary>
   [Theory]
   [InlineData(HttpStatusCode.NotFound, "does not offer the operator printer catalog")]
   [InlineData(HttpStatusCode.Forbidden, "may not read the operator printer catalog (HTTP 403 Forbidden)")]
   public async Task UnreadableCatalog_ReportsNotReportedWithTheReason(HttpStatusCode status, string reason)
   {
      await using RealtimeRuntimeFixture fixture = new();
      fixture.CatalogStatus = status;
      await using RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => string.Equals(fixture.Runtime.GetSnapshot().PrinterSync?.Outcome, PrinterServerSync.NotReported, StringComparison.Ordinal));
      AgentSnapshot snapshot = fixture.Runtime.GetSnapshot();
      Assert.All(snapshot.Printers, printer => Assert.Equal(PrinterServerState.NotReported, printer.ServerState));
      Assert.Contains(reason, snapshot.PrinterSync!.Detail, StringComparison.Ordinal);
      Assert.Single(fixture.Logs, line => line.Contains("Printer approval is not reported by", StringComparison.Ordinal));
      Assert.True(snapshot.Connected);
   }



   /// <summary>A reading is kept for a minute: the heartbeat-driven synchronization passes do not read the catalog again straight away.</summary>
   [Fact]
   public async Task Catalog_IsNotReadOnEverySynchronizationPass()
   {
      await using RealtimeRuntimeFixture fixture = new();
      await using RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => string.Equals(fixture.Runtime.GetSnapshot().PrinterSync?.Outcome, PrinterServerSync.Reported, StringComparison.Ordinal));
      int reads = fixture.CatalogRequests;
      await Task.Delay(1500);
      Assert.Equal(reads, fixture.CatalogRequests);
   }



   /// <summary>
   /// On a printing 2.4 server the client advertises printer-approval-v1 and takes approval from the server's PRINTERS frame: the
   /// catalog is never read, a disabled or unlisted shared printer is awaiting approval, and a later frame (an administrator approving
   /// the printer) updates the state at once.
   /// </summary>
   [Fact]
   public async Task PrintersFrame_IsAuthoritativeOnA24Server()
   {
      await using RealtimeRuntimeFixture fixture = new(printingVersion: "2.4.0");
      await using RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      Assert.Contains("printer-approval-v1", fixture.LastHelloCapabilities, StringComparer.Ordinal);
      await fixture.WaitAsync(() => fixture.Messages.Any(message => message.Type is "INVENTORY"));
      await connection.SendAsync("PRINTERS", new { printers = new object[] { new { printerId = "printer", enabled = true } } });
      await fixture.WaitAsync(() => string.Equals(fixture.Runtime.GetSnapshot().PrinterSync?.Outcome, PrinterServerSync.Reported, StringComparison.Ordinal));
      AgentSnapshot snapshot = fixture.Runtime.GetSnapshot();
      Assert.Equal(PrinterServerSync.RealtimeSource, snapshot.PrinterSync!.Source);
      Assert.Equal(PrinterServerState.Approved, snapshot.Printers.Single(printer => string.Equals(printer.PrinterId, "printer", StringComparison.Ordinal)).ServerState);
      Assert.Equal(PrinterServerState.AwaitingApproval, snapshot.Printers.Single(printer => string.Equals(printer.PrinterId, "other", StringComparison.Ordinal)).ServerState);
      await connection.SendAsync("PRINTERS", new { printers = new object[] { new { printerId = "printer", enabled = true }, new { printerId = "other", enabled = true } } });
      await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Printers.All(printer => string.Equals(printer.ServerState, PrinterServerState.Approved, StringComparison.Ordinal)));
      await connection.SendAsync("PRINTERS", new { printers = new object[] { new { printerId = "printer", enabled = false }, new { printerId = "other", enabled = true } } });
      await fixture.WaitAsync(() => string.Equals(fixture.Runtime.GetSnapshot().Printers.Single(printer => string.Equals(printer.PrinterId, "printer", StringComparison.Ordinal)).ServerState, PrinterServerState.AwaitingApproval, StringComparison.Ordinal));
      Assert.Equal(0, fixture.CatalogRequests);
      Assert.Contains(fixture.Logs, line => line.Contains("Printer approval pushed by", StringComparison.Ordinal) && line.Contains("1 of 1 shared printer(s) approved", StringComparison.Ordinal));
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>A PRINTERS frame without a printers list is a protocol violation that ends the connection, like any other malformed frame; the catalog is still not consulted on a 2.4 server.</summary>
   [Fact]
   public async Task MalformedPrintersFrame_EndsTheConnection()
   {
      await using RealtimeRuntimeFixture fixture = new(printingVersion: "2.4.0");
      RealtimeRuntimeFixture.Connection first = await fixture.ConnectAsync();
      await first.SendAsync("PRINTERS", new { nothing = true });
      await fixture.WaitAsync(() => !fixture.Runtime.GetSnapshot().Connected);
      await using RealtimeRuntimeFixture.Connection second = await fixture.ConnectAsync();
      Assert.Equal(0, fixture.CatalogRequests);
      Assert.Contains(fixture.Logs, line => line.Contains("PRINTERS frame has no printers list", StringComparison.Ordinal));
   }



   /// <summary>
   /// The reason logged for a connection the reader ended is the frame it rejected, even while a send is waiting for an
   /// acknowledgement. The server holds back the inventory acknowledgement and then sends the malformed frame, so the channel
   /// fails that pending send at the same moment; the log must still name the frame, not the secondary "closed before
   /// acknowledgement" failure. The failure is logged before the service reconnects, so the test waits for that line.
   /// </summary>
   [Fact]
   public async Task MalformedPrintersFrame_IsTheLoggedReasonEvenWhileASendAwaitsAcknowledgement()
   {
      await using RealtimeRuntimeFixture fixture = new(printingVersion: "2.4.0");
      var inventoryPending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      fixture.BeforeAcknowledgement = async message =>
      {
         if(message.Type is "INVENTORY")
         {
            inventoryPending.TrySetResult();
            await release.Task;
         }
      };
      try
      {
         RealtimeRuntimeFixture.Connection first = await fixture.ConnectAsync();
         await fixture.WaitAsync(() => inventoryPending.Task.IsCompleted);
         await first.SendAsync("PRINTERS", new { nothing = true });
         await fixture.WaitAsync(() => fixture.Logs.Any(line => line.Contains("Realtime connection stopped", StringComparison.Ordinal)));
         string stopped = fixture.Logs.First(line => line.Contains("Realtime connection stopped", StringComparison.Ordinal));
         Assert.Contains("PRINTERS frame has no printers list", stopped, StringComparison.Ordinal);
         Assert.DoesNotContain("before acknowledgement", stopped, StringComparison.Ordinal);
      }
      finally { release.TrySetResult(); }
   }



   /// <summary>Losing the connection resets the reading to "not checked", and the next connection asks the server again.</summary>
   [Fact]
   public async Task Reconnect_StartsFromNotCheckedAndAsksAgain()
   {
      await using RealtimeRuntimeFixture fixture = new();
      RealtimeRuntimeFixture.Connection first = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => string.Equals(fixture.Runtime.GetSnapshot().PrinterSync?.Outcome, PrinterServerSync.Reported, StringComparison.Ordinal));
      int reads = fixture.CatalogRequests;
      first.Abort();
      await fixture.WaitAsync(() => string.Equals(fixture.Runtime.GetSnapshot().PrinterSync?.Outcome, PrinterServerSync.NotChecked, StringComparison.Ordinal));
      Assert.All(fixture.Runtime.GetSnapshot().Printers.Where(printer => printer.Exposed), printer => Assert.Equal(PrinterServerState.NotChecked, printer.ServerState));
      await using RealtimeRuntimeFixture.Connection second = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => fixture.CatalogRequests > reads);
      await fixture.WaitAsync(() => string.Equals(fixture.Runtime.GetSnapshot().PrinterSync?.Outcome, PrinterServerSync.Reported, StringComparison.Ordinal));
   }
}
