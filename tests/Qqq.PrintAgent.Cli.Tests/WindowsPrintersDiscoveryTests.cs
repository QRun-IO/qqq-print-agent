/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Printer discovery run against a Windows that exists only in memory: which queues are listed, how each is identified
/// and classified, which formats it is offered with, and what a failing spooler turns into. Each test asserts the calls
/// sent and their order, which is the evidence that discovery reaches Windows the way it did before the calls were moved
/// behind <see cref="ISpooler"/>. No print queue on this computer is listed or opened.
/// </summary>
public sealed class WindowsPrintersDiscoveryTests
{
   /// <summary>A spooler with nothing installed answers the sizing call with success and no size; discovery returns an empty list without a second call.</summary>
   [Fact]
   public void NothingInstalled_IsAnEmptyInventoryFromOneCall()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Setup();
      Assert.Empty(printers.Discover());
      Assert.Equal(["EnumPrintersSize(6)"], windows.Calls, StringComparer.Ordinal);
   }



   /// <summary>
   /// Local queues and connections (flags 6) are listed in spooler order. A label queue with a ZDesigner driver is offered
   /// PDF and ZPL with its default media; a busy laser queue without readable defaults is offered PDF, is READY and has no
   /// media; a print-to-PDF queue and a FILE: port are UNKNOWN with no formats whatever their status, and their settings are
   /// never read. The identifier is the first 24 hexadecimal digits of the SHA-256 of the queue name.
   /// </summary>
   [Fact]
   public void QueuesAreListedIdentifiedAndClassified()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Setup();
      byte[] defaults = SyntheticDevMode.Label();
      windows.Add(new FakeQueue("Synthetic Zebra queue", "ZDesigner ZT411-300dpi ZPL", "USB001") { MachineDefaults = defaults }.WithContext(1200, 1800));
      windows.Add(new FakeQueue("Synthetic laser queue", "Synthetic laser driver", "192.0.2.10") { Status = 0x400 });
      windows.Add(new FakeQueue("Document writer (synthetic)", "Microsoft Print To PDF", "PORTPROMPT:") { MachineDefaults = defaults });
      windows.Add(new FakeQueue("Print to file (synthetic)", "Generic / Text Only", "FILE:") { Status = 0x80, MachineDefaults = defaults });

      IReadOnlyList<PrinterInfo> found = printers.Discover();

      Assert.Equal(["6119358ca3e4aafc30d6a3da", "f00632792c722747d33552e2", "6c32da906f525a7720e3104d", "bc61693d292c6ddf47a614c6"], found.Select(printer => printer.PrinterId), StringComparer.Ordinal);
      Assert.Equal(["Synthetic Zebra queue", "Synthetic laser queue", "Document writer (synthetic)", "Print to file (synthetic)"], found.Select(printer => printer.Name), StringComparer.Ordinal);
      Assert.Equal(["ZDesigner ZT411-300dpi ZPL", "Synthetic laser driver", "Microsoft Print To PDF", "Generic / Text Only"], found.Select(printer => printer.DriverName), StringComparer.Ordinal);
      Assert.Equal(["USB001", "192.0.2.10", "PORTPROMPT:", "FILE:"], found.Select(printer => printer.PortName), StringComparer.Ordinal);
      Assert.Equal(["READY", "READY", "UNKNOWN", "UNKNOWN"], found.Select(printer => printer.Status), StringComparer.Ordinal);
      Assert.Equal(["PDF,ZPL", "PDF", "", ""], found.Select(printer => string.Join(',', printer.Formats)), StringComparer.Ordinal);
      Assert.Equal(WindowsPrinters.BuildMediaProfile("6119358ca3e4aafc30d6a3da", "ZDesigner ZT411-300dpi ZPL", defaults, 101600, 152400, 300, 300), Assert.Single(found[0].MediaProfiles!));
      Assert.All(found.Skip(1), printer => Assert.Empty(printer.MediaProfiles!));
      Assert.Equal(
      [
         "EnumPrintersSize(6)", "EnumPrinters(6, 544)",
         "OpenPrinter(Synthetic Zebra queue)", "GetPrinterSize(0x201, 8)", "GetPrinter(0x201, 8, 232)", "ClosePrinter(0x201)",
         "CreateDC(WINSPOOL, Synthetic Zebra queue, 224 bytes)", "GetDeviceCaps(0xd01, 88)", "GetDeviceCaps(0xd01, 90)", "DeleteDC(0xd01)",
         "OpenPrinter(Synthetic laser queue)", "GetPrinterSize(0x202, 8)", "ClosePrinter(0x202)",
      ], windows.Calls, StringComparer.Ordinal);
      Assert.Equal(0, windows.OpenPrinters);
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>A queue that can print unattended keeps the health word its spooler status maps to, so the operator sees why it is unavailable.</summary>
   [Theory]
   [InlineData(0x80u, "OFFLINE")]
   [InlineData(0x1u, "PAUSED")]
   [InlineData(0x8u, "ERROR")]
   [InlineData(0x800000u, "UNKNOWN")]
   [InlineData(0x400u, "READY")]
   public void UnattendedQueueKeepsItsHealth(uint status, string expected)
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Setup();
      windows.Add(new FakeQueue("Synthetic laser queue") { Status = status });
      PrinterInfo found = Assert.Single(printers.Discover());
      Assert.Equal(expected, found.Status);
      Assert.Equal(["PDF"], found.Formats, StringComparer.Ordinal);
   }



   /// <summary>ZPL is offered only when the queue name or driver names Zebra, or the driver is a ZDesigner one, in any letter case; every other unattended queue gets PDF alone.</summary>
   [Theory]
   [InlineData("Warehouse ZEBRA 3", "Generic / Text Only", true)]
   [InlineData("Dock label 1", "zebra technologies zt230", true)]
   [InlineData("Dock label 1", "ZDesigner GK420d", true)]
   [InlineData("Dock label 1", "zdesigner gk420d", true)]
   [InlineData("Dock label 1", "Synthetic label driver", false)]
   [InlineData("Front office", "Synthetic laser driver", false)]
   public void ZplIsOfferedOnlyToZebraQueues(string name, string driver, bool zpl)
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Setup();
      windows.Add(new FakeQueue(name, driver));
      Assert.Equal(zpl ? ["PDF", "ZPL"] : new[] { "PDF" }, Assert.Single(printers.Discover()).Formats, StringComparer.Ordinal);
   }



   /// <summary>A queue whose driver needs a person (XPS, OneNote, fax) is never offered a format, whatever its port.</summary>
   [Theory]
   [InlineData("Microsoft XPS Document Writer v4")]
   [InlineData("Send to Microsoft OneNote 16 Driver")]
   [InlineData("Microsoft Shared Fax Driver")]
   public void DriverThatNeedsAPersonIsNeverOfferedAFormat(string driver)
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Setup();
      windows.Add(new FakeQueue("Synthetic interactive queue", driver, "USB001") { MachineDefaults = SyntheticDevMode.Label() });
      PrinterInfo found = Assert.Single(printers.Discover());
      Assert.Equal("UNKNOWN", found.Status);
      Assert.Empty(found.Formats);
      Assert.Empty(found.MediaProfiles!);
      Assert.Equal(["EnumPrintersSize(6)", "EnumPrinters(6, 136)"], windows.Calls, StringComparer.Ordinal);
   }



   /// <summary>A queue the spooler lists without a name, driver or port is reported with empty strings and the identifier of the empty name; nothing is null.</summary>
   [Fact]
   public void NamesTheSpoolerLeftOutBecomeEmptyStrings()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Setup();
      windows.Add(new FakeQueue(null, null, null));
      PrinterInfo found = Assert.Single(printers.Discover());
      Assert.Equal(("e3b0c44298fc1c149afbf4c8", "", "", ""), (found.PrinterId, found.Name, found.DriverName, found.PortName));
      Assert.Equal(["PDF"], found.Formats, StringComparer.Ordinal);
      Assert.Equal(["EnumPrintersSize(6)", "EnumPrinters(6, 136)", "OpenPrinter()"], windows.Calls, StringComparer.Ordinal);
   }



   /// <summary>
   /// A sizing call that fails for any reason but "buffer too small with a size" is thrown with its Windows error and the
   /// list is never read, so a broken spooler is not mistaken for an empty inventory that would withdraw every printer.
   /// </summary>
   [Theory]
   [InlineData(1722, 0u)]
   [InlineData(5, 0u)]
   [InlineData(5, 512u)]
   [InlineData(122, 0u)]
   public void FailedSizingCall_IsThrownAndTheListIsNeverRead(int error, uint reported)
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Setup();
      windows.Add(new FakeQueue("Synthetic laser queue"));
      windows.ReportedListSize = reported;
      windows.Fail("EnumPrintersSize", error);
      Win32Exception failure = Assert.Throws<Win32Exception>(() => printers.Discover());
      Assert.Equal(error, failure.NativeErrorCode);
      Assert.Equal(["EnumPrintersSize(6)"], windows.Calls, StringComparer.Ordinal);
   }



   /// <summary>A list read that fails after a good sizing call is thrown with its Windows error, and no queue is opened.</summary>
   [Fact]
   public void FailedListRead_IsThrownWithItsWindowsError()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Setup();
      windows.Add(new FakeQueue("Synthetic laser queue"));
      windows.Fail("EnumPrinters", 1722);
      Win32Exception failure = Assert.Throws<Win32Exception>(() => printers.Discover());
      Assert.Equal(1722, failure.NativeErrorCode);
      Assert.Equal(["EnumPrintersSize(6)", "EnumPrinters(6, 136)"], windows.Calls, StringComparer.Ordinal);
   }



   /// <summary>A size no buffer can have is an overflow before the list is read, not a truncated request.</summary>
   [Fact]
   public void ImpossibleListSize_IsRefusedBeforeTheListIsRead()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Setup();
      windows.ReportedListSize = 2_147_483_648;
      Assert.Throws<OverflowException>(() => printers.Discover());
      Assert.Equal(["EnumPrintersSize(6)"], windows.Calls, StringComparer.Ordinal);
   }



   /// <summary>Builds the rules over a fake Windows with no queues yet.</summary>
   private static (WindowsPrinters Printers, FakeWindowsPrinting Windows) Setup()
   {
      FakeWindowsPrinting windows = new();
      return (new WindowsPrinters(windows, windows, windows, new FakePdfRenderer(windows.Calls)), windows);
   }
}
