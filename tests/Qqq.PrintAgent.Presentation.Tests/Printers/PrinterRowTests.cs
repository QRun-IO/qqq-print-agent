/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Printers;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.Printers;

/// <summary>What a printer row, its expanded details and its controls show for a snapshot from the service.</summary>
public sealed class PrinterRowTests
{
   /// <summary>A printer with the operator's own name, a location and formats shows them, keeps the Windows queue name in the tooltip, and names the share checkbox after the friendly name.</summary>
   [Fact]
   public void NamedPrinter_ShowsItsLabelsAndKeepsTheQueueNameInTheToolTip()
   {
      PrinterSnapshot printer = TestData.Printer(name: "ZDesigner ZT411", formats: ["PDF", "ZPL"], exposed: true, displayName: "Dock 4 labels", location: "Dock 4", serverState: PrinterServerState.Approved);

      var row = PrinterRow.From(printer, new PrinterServerSync(PrinterServerSync.Reported, Server: "print.example.com", Source: PrinterServerSync.RealtimeSource), "QQQ");

      Assert.Equal("Dock 4 labels", row.Name);
      Assert.Equal("Ready", row.Status);
      Assert.Equal("Approved", row.Server);
      Assert.Equal("Dock 4", row.Location);
      Assert.Equal("PDF, ZPL", row.Formats);
      Assert.Equal("Dock 4 labels\nWindows queue: ZDesigner ZT411", row.NameToolTip);
      Assert.Equal("Ready", row.StatusToolTip);
      Assert.Equal("QQQ (print.example.com) approved this printer for this computer; it can receive jobs. The server pushes a change the moment an administrator makes it.", row.ServerDetail);
      Assert.Equal("Share Dock 4 labels with the server", row.ShareName);
   }



   /// <summary>A printer without a friendly name, location or usable format falls back to the queue name and dashes, and its status tooltip explains why it cannot be used unattended.</summary>
   [Fact]
   public void BarePrinter_FallsBackToTheQueueNameAndExplainsWhyItIsUnavailable()
   {
      PrinterSnapshot printer = TestData.Printer(name: "Microsoft Print to PDF", formats: [], displayName: "  ", location: " ");

      var row = PrinterRow.From(printer, sync: null, "QQQ");

      Assert.Equal("Microsoft Print to PDF", row.Name);
      Assert.Equal("Interactive only", row.Status);
      Assert.Equal("—", row.Location);
      Assert.Equal("—", row.Formats);
      Assert.Equal("Unavailable for unattended service printing. Select this printer for details.", row.StatusToolTip);
      Assert.Equal("Not checked", row.Server);
      Assert.Equal("Not checked yet with QQQ. The service asks the server after it connects and sends the inventory.", row.ServerDetail);
   }



   /// <summary>A printer with no usable format reads "Offline" when the spooler says so and "Interactive only" otherwise, never a misleading ready state; one with a format uses the shared status wording.</summary>
   [Theory]
   [InlineData("OFFLINE", false, "Offline")]
   [InlineData("READY", false, "Interactive only")]
   [InlineData("PAUSED", true, "Paused")]
   [InlineData("ERROR", true, "Needs attention")]
   public void StatusLabel_NeverShowsReadyForAPrinterThatCannotPrintUnattended(string status, bool hasFormat, string expected)
      => Assert.Equal(expected, PrinterRow.StatusLabel(TestData.Printer(status: status, formats: hasFormat ? ["PDF"] : [])));



   /// <summary>Each server-approval state has its own word, and an unknown state reads "Not checked".</summary>
   [Theory]
   [InlineData(PrinterServerState.Approved, "Approved")]
   [InlineData(PrinterServerState.AwaitingApproval, "Awaiting approval")]
   [InlineData(PrinterServerState.NotReported, "Not reported")]
   [InlineData(PrinterServerState.NotShared, "Not shared")]
   [InlineData(PrinterServerState.NotChecked, "Not checked")]
   [InlineData("SOMETHING_NEW", "Not checked")]
   public void ServerLabel_HasOneWordPerState(string state, string expected)
      => Assert.Equal(expected, PrinterRow.ServerLabel(TestData.Printer(serverState: state)));



   /// <summary>The sentence behind the approval word says what the state means, names the server, and says how fresh the reading is; a failed reading quotes the service's reason, or a general one when there is none.</summary>
   [Fact]
   public void ServerDetail_ExplainsEachStateAndNamesTheServer()
   {
      PrinterServerSync catalog = new(PrinterServerSync.Reported, Server: "print.example.com", Source: PrinterServerSync.CatalogSource);

      Assert.Equal("Forge (print.example.com) received this printer in the inventory but has not approved it. An administrator approves it on the server (Printing → Print devices). Checked about once a minute while connected.",
         PrinterRow.ServerDetailText(TestData.Printer(serverState: PrinterServerState.AwaitingApproval), catalog, "Forge"));
      Assert.Equal("Forge did not report approval: The signed-in user may not read the printer catalog. Approve or check the printer on the server.",
         PrinterRow.ServerDetailText(TestData.Printer(serverState: PrinterServerState.NotReported), new PrinterServerSync(PrinterServerSync.NotReported, Detail: "The signed-in user may not read the printer catalog."), "Forge"));
      Assert.Equal("Forge did not report approval: the catalog could not be read. Approve or check the printer on the server.",
         PrinterRow.ServerDetailText(TestData.Printer(serverState: PrinterServerState.NotReported), sync: null, "Forge"));
      Assert.Equal("Not shared with the server. Turn on Shared to offer it; the server then has to approve it.",
         PrinterRow.ServerDetailText(TestData.Printer(serverState: PrinterServerState.NotShared), catalog, "Forge"));
   }



   /// <summary>The expanded details show the queue name, the location and description, and the driver, port and identifier; a blank location or description reads "Not set".</summary>
   [Fact]
   public void Details_ShowQueueAndDiagnosticsAndSayNotSetForBlanks()
   {
      Assert.Equal(new PrinterDetails("Front Desk Laser", "Not set", "Not set", "Driver: Generic Driver\nPort: USB001\nPrinter ID: printer-1"),
         PrinterDetails.From(TestData.Printer()));
      Assert.Equal(new PrinterDetails("Front Desk Laser", "Reception", "Prints visitor badges.", "Driver: Generic Driver\nPort: USB001\nPrinter ID: printer-1"),
         PrinterDetails.From(TestData.Printer(location: "Reception", description: "Prints visitor badges.")));
   }



   /// <summary>Sharing can be turned on only for a printer that can print unattended, can always be turned off for one that is shared, and nothing can be changed while a request runs or before the row has a printer.</summary>
   [Theory]
   [InlineData(true, false, false, true)]
   [InlineData(false, true, false, true)]
   [InlineData(false, false, false, false)]
   [InlineData(true, true, true, false)]
   public void Sharing_IsOfferedOnlyWhereItCanTakeEffect(bool hasFormat, bool exposed, bool busy, bool expected)
   {
      Assert.Equal(expected, PrinterActions.CanShare(TestData.Printer(formats: hasFormat ? ["PDF"] : [], exposed: exposed), busy));
      Assert.False(PrinterActions.CanShare(null, busy: false));
   }



   /// <summary>A test print needs a shared printer with a format and no running request; editing details only needs no running request.</summary>
   [Theory]
   [InlineData(true, true, false, true)]
   [InlineData(true, false, false, false)]
   [InlineData(false, true, false, false)]
   [InlineData(true, true, true, false)]
   public void TestPrint_NeedsASharedPrinterWithAFormat(bool hasFormat, bool exposed, bool busy, bool expected)
   {
      Assert.Equal(expected, PrinterActions.CanTest(TestData.Printer(formats: hasFormat ? ["ZPL"] : [], exposed: exposed), busy));
      Assert.False(PrinterActions.CanTest(null, busy: false));
      Assert.Equal(!busy, PrinterActions.CanEdit(busy));
   }
}
