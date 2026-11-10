/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Printers;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.Printers;

/// <summary>The line above the printer table and the hint in an empty table.</summary>
public sealed class PrintersSummaryTests
{
   /// <summary>With nothing shared the line only counts, because approval does not apply to an unshared printer.</summary>
   [Fact]
   public void NothingShared_OnlyCounts()
      => Assert.Equal("2 printers · 0 shared", PrintersSummary.Text(TestData.Snapshot([TestData.Printer(id: "a"), TestData.Printer(id: "b")], sync: new PrinterServerSync(PrinterServerSync.Reported)), "QQQ"));



   /// <summary>When the server reported approvals the line counts the approved printers, and adds the number still awaiting approval only when there are any.</summary>
   [Fact]
   public void ReportedApprovals_AreCounted()
   {
      PrinterSnapshot approved = TestData.Printer(id: "a", exposed: true, serverState: PrinterServerState.Approved);
      PrinterSnapshot awaiting = TestData.Printer(id: "b", exposed: true, serverState: PrinterServerState.AwaitingApproval);
      PrinterSnapshot local = TestData.Printer(id: "c", serverState: PrinterServerState.NotShared);
      PrinterServerSync reported = new(PrinterServerSync.Reported);

      Assert.Equal("3 printers · 2 shared · 1 approved by QQQ · 1 awaiting approval", PrintersSummary.Text(TestData.Snapshot([approved, awaiting, local], sync: reported), "QQQ"));
      Assert.Equal("2 printers · 1 shared · 1 approved by Forge", PrintersSummary.Text(TestData.Snapshot([approved, local], sync: reported), "Forge"));
   }



   /// <summary>When the server could not report approval, or has not been asked yet, the line says so instead of counting.</summary>
   [Fact]
   public void UnknownApproval_IsSaidInsteadOfCounted()
   {
      PrinterSnapshot[] printers = [TestData.Printer(exposed: true)];

      Assert.Equal("1 printers · 1 shared · approval not reported by QQQ", PrintersSummary.Text(TestData.Snapshot(printers, sync: new PrinterServerSync(PrinterServerSync.NotReported)), "QQQ"));
      Assert.Equal("1 printers · 1 shared · approval not checked with QQQ yet", PrintersSummary.Text(TestData.Snapshot(printers, sync: PrinterServerSync.Initial), "QQQ"));
      Assert.Equal("1 printers · 1 shared · approval not checked with QQQ yet", PrintersSummary.Text(TestData.Snapshot(printers), "QQQ"));
   }



   /// <summary>An empty table tells an enrolled device to add a printer in Windows, and a device that is not enrolled to connect first.</summary>
   [Fact]
   public void EmptyTable_SaysWhatToDoNext()
   {
      Assert.Equal("Add a printer in Windows Settings, then refresh this list.", PrintersSummary.EmptyText(enrolled: true));
      Assert.Equal("Connect this device to the server, then choose the printers to share.", PrintersSummary.EmptyText(enrolled: false));
   }
}
