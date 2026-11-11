/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Printing a staged file on a station that has other queues beside the job's own, run against a Windows that exists
/// only in memory. One of the other queues has a driver that never answers, which used to stop every job on the station;
/// the other is an ordinary queue with settings of its own. Each test prints the same job twice, once on a station that
/// has only the job's queue and once on the station of three, and requires the calls sent to differ in one thing only:
/// the size of the list the spooler was asked for. Everything sent for the job's own queue (the read of its settings
/// before the document, the media check, the document itself, the cleanup) is call for call what a one-queue station
/// gets, and nothing at all is sent for the other two. The staged files are synthetic and nothing is printed.
/// </summary>
public sealed class WindowsPrintersOneQueueTests
{
   /// <summary>The name of the queue the ZPL job is for.</summary>
   private const string Zebra = "Synthetic Zebra queue";



   /// <summary>The identifier of that queue: the first 24 hexadecimal digits of the SHA-256 of its name.</summary>
   private const string ZebraId = "6119358ca3e4aafc30d6a3da";



   /// <summary>The name of the queue the PDF job is for.</summary>
   private const string LabelQueue = "Synthetic label queue";



   /// <summary>The driver of that queue.</summary>
   private const string LabelDriver = "Synthetic label driver";



   /// <summary>The identifier of that queue.</summary>
   private const string LabelId = "72a900462ca6a6e18ecb8fb2";



   /// <summary>The name of the queue whose driver never answers.</summary>
   private const string Broken = "Synthetic broken queue";



   /// <summary>The name of the laser queue, which nothing is wrong with and which no job here is for.</summary>
   private const string Third = "Synthetic laser queue";



   /// <summary>A synthetic ZPL label.</summary>
   private static readonly byte[] Label = Encoding.ASCII.GetBytes("^XA^FO50,50^FDsynthetic^FS^XZ");



   /// <summary>Synthetic bytes staged as a PDF; the fake renderer never reads them.</summary>
   private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-synthetic; the fake renderer never reads this");



   /// <summary>
   /// A ZPL job for the Zebra queue, on a station where a queue whose driver never answers is listed before it and a laser
   /// queue after it: the queues are listed, the Zebra queue's settings are read, and the document is opened, started,
   /// written, ended and closed. Neither other queue appears in any call, and the job is spooled.
   /// </summary>
   [Fact]
   public async Task ZplJobAsksOnlyItsOwnQueueAndIsSpooled()
   {
      FakeWindowsPrinting alone = await PrintAsync(otherQueues: false, new FakeQueue(Zebra, "ZDesigner ZT411-300dpi ZPL", "USB001"), ZebraId, "ZPL", Label);
      FakeWindowsPrinting station = await PrintAsync(otherQueues: true, new FakeQueue(Zebra, "ZDesigner ZT411-300dpi ZPL", "USB001"), ZebraId, "ZPL", Label);

      Assert.Equal(
      [
         "EnumPrintersSize(6)", "EnumPrinters(6, 408)",
         $"OpenPrinter({Zebra})", "GetPrinterSize(0x201, 8)", "ClosePrinter(0x201)",
         $"OpenPrinter({Zebra})", "StartDocPrinter(0x202, QQQ job-1, RAW)", $"WritePrinter(0x202, {Label.Length} bytes, {Label.Length})", "EndDocPrinter(0x202)", "ClosePrinter(0x202)",
      ], station.Calls, StringComparer.Ordinal);
      AssertOnlyTheListSizeDiffers(alone, station);
      FakeSpoolDocument document = Assert.Single(station.RawDocuments);
      Assert.Equal((Zebra, "QQQ job-1", "RAW", true, false), (document.Queue, document.Name, document.DataType, document.Ended, document.Aborted));
      Assert.Equal(Label, document.Bytes.ToArray());
      Assert.Equal(0, station.OpenPrinters);
   }



   /// <summary>
   /// A PDF job that selected its queue's default media, on the same kind of station: the queues are listed, the label
   /// queue's settings are read, and then everything the selected-media path sends for that queue (the settings read
   /// again, the driver's merge, the resolved context, the checked context, the document and its page) follows, exactly
   /// as on a station with one queue. Neither other queue appears in any call, and the job is spooled.
   /// </summary>
   [Fact]
   public async Task PdfJobWithSelectedMediaAsksOnlyItsOwnQueueAndIsSpooled()
   {
      byte[] defaults = SyntheticDevMode.Label();
      PrinterMediaProfile profile = WindowsPrinters.BuildMediaProfile(LabelId, LabelDriver, defaults, 101600, 152400, 300, 300)!;
      PrintOptions selection = new(MediaProfile: new PrinterMediaSelection(profile.Id, profile.Revision));
      FakeWindowsPrinting alone = await PrintAsync(otherQueues: false, new FakeQueue(LabelQueue, LabelDriver, "USB001") { MachineDefaults = defaults }.WithContext(1200, 1800), LabelId, "PDF", Pdf, selection);
      FakeWindowsPrinting station = await PrintAsync(otherQueues: true, new FakeQueue(LabelQueue, LabelDriver, "USB001") { MachineDefaults = defaults }.WithContext(1200, 1800), LabelId, "PDF", Pdf, selection);

      Assert.Equal(
      [
         "EnumPrintersSize(6)", "EnumPrinters(6, 408)",
         $"OpenPrinter({LabelQueue})", "GetPrinterSize(0x201, 8)", "GetPrinter(0x201, 8, 232)", "ClosePrinter(0x201)",
         $"CreateDC(WINSPOOL, {LabelQueue}, 224 bytes)", "GetDeviceCaps(0xd01, 88)", "GetDeviceCaps(0xd01, 90)", "DeleteDC(0xd01)",
         "LoadPdf",
         $"OpenPrinter({LabelQueue})", "GetPrinterSize(0x202, 8)", "GetPrinter(0x202, 8, 232)", "ClosePrinter(0x202)",
         $"CreateDC(WINSPOOL, {LabelQueue}, 224 bytes)", "GetDeviceCaps(0xd02, 88)", "GetDeviceCaps(0xd02, 90)", "DeleteDC(0xd02)",
         $"OpenPrinter({LabelQueue})", $"DocumentPropertiesSize(0x203, {LabelQueue})", $"DocumentProperties(0x203, {LabelQueue}, 224 bytes, 224 bytes, 10)", "ClosePrinter(0x203)",
         $"CreateDC(WINSPOOL, {LabelQueue}, 224 bytes)", "GetDeviceCaps(0xd03, 88)", "GetDeviceCaps(0xd03, 90)", "DeleteDC(0xd03)",
         $"CreateDC(WINSPOOL, {LabelQueue}, 224 bytes)", "GetDeviceCaps(0xd04, 88)", "GetDeviceCaps(0xd04, 90)", "GetDeviceCaps(0xd04, 110)", "GetDeviceCaps(0xd04, 111)",
         "StartDoc(0xd04, QQQ job-1)",
         "GetPage(0)", "RenderPage(0, 600, 900)", "GetDeviceCaps(0xd04, 88)", "GetDeviceCaps(0xd04, 90)", "GetDeviceCaps(0xd04, 112)", "GetDeviceCaps(0xd04, 113)",
         "StartPage(0xd04)", "SetStretchBltMode(0xd04, 4)", "StretchDIBits(0xd04, 0, 0, 1200, 1800, 0, 0, 600, 900, 4 bytes, 600x-900 planes 1 bits 32, 0, 0xcc0020)", "EndPage(0xd04)", "DisposePage(0)",
         "EndDoc(0xd04)", "DeleteDC(0xd04)",
      ], station.Calls, StringComparer.Ordinal);
      AssertOnlyTheListSizeDiffers(alone, station);
      FakeGdiDocument document = Assert.Single(station.GdiDocuments);
      Assert.Equal((LabelQueue, "QQQ job-1", 1, 1, true, false), (document.Queue, document.Name, document.PagesStarted, document.PagesEnded, document.Ended, document.Aborted));
      Assert.Equal(0, station.OpenPrinters);
      Assert.Equal(0, station.OpenContexts);
   }



   /// <summary>
   /// A job whose own queue is gone from a station that still has other queues fails once the queues were listed, with
   /// the error it has always failed with (the words of a search that found nothing, which the service records as the
   /// job's outcome), and no queue is opened: the queues that remain are not the job's.
   /// </summary>
   [Fact]
   public async Task JobWhoseQueueIsGoneAsksNoOtherQueue()
   {
      FakeWindowsPrinting windows = Station(new FakeQueue(Zebra, "ZDesigner ZT411-300dpi ZPL", "USB001"));
      WindowsPrinters printers = new(windows, windows, windows, new FakePdfRenderer(windows.Calls));
      using StagedDocument staged = new(Label);

      InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => printers.PrintFileAsync(staged.Job("000000000000000000000000", "ZPL"), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));

      Assert.Equal(Assert.Throws<InvalidOperationException>(() => Array.Empty<PrinterInfo>().Single(item => item is not null)).Message, failure.Message);
      Assert.Equal(["EnumPrintersSize(6)", "EnumPrinters(6, 408)"], windows.Calls, StringComparer.Ordinal);
      Assert.Empty(windows.RawDocuments);
   }



   /// <summary>
   /// Prints one staged file for the given queue and returns the fake Windows it was printed on: either a station that
   /// has that queue alone, or one where a queue whose driver never answers is listed before it and a laser queue after it.
   /// </summary>
   private static async Task<FakeWindowsPrinting> PrintAsync(bool otherQueues, FakeQueue target, string printerId, string format, byte[] content, PrintOptions? options = null)
   {
      FakeWindowsPrinting windows = otherQueues ? Station(target) : new FakeWindowsPrinting();
      if(!otherQueues)
      {
         _ = windows.Add(target);
      }
      WindowsPrinters printers = new(windows, windows, windows, new FakePdfRenderer(windows.Calls));
      using StagedDocument staged = new(content);
      _ = await printers.PrintFileAsync(staged.Job(printerId, format, options), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None);
      return windows;
   }



   /// <summary>
   /// A station of three queues in the order the spooler lists them: one whose driver never answers, the given queue, and
   /// a laser queue. The two others have machine defaults and a device context, so a read that asked them would be seen.
   /// </summary>
   private static FakeWindowsPrinting Station(FakeQueue target)
   {
      FakeWindowsPrinting windows = new();
      _ = windows.Add(new FakeQueue(Broken, "Synthetic stuck driver", "192.0.2.20") { MachineDefaults = SyntheticDevMode.Label(), NeverAnswers = () => new TimeoutException("Synthetic: this queue's driver never answers.") });
      _ = windows.Add(target);
      _ = windows.Add(new FakeQueue(Third, "Synthetic laser driver", "192.0.2.10") { MachineDefaults = SyntheticDevMode.Label() }.WithContext(1200, 1800));
      return windows;
   }



   /// <summary>
   /// Requires the calls sent on the station of three to be the calls sent on the one-queue station, in the same order
   /// with the same arguments, except for the size of the list in the second call (136 bytes a queue), and requires that
   /// no call names either of the other two queues.
   /// </summary>
   private static void AssertOnlyTheListSizeDiffers(FakeWindowsPrinting alone, FakeWindowsPrinting station)
   {
      Assert.Equal(["EnumPrintersSize(6)", "EnumPrinters(6, 136)"], alone.Calls.Take(2), StringComparer.Ordinal);
      Assert.Equal(["EnumPrintersSize(6)", "EnumPrinters(6, 408)"], station.Calls.Take(2), StringComparer.Ordinal);
      Assert.Equal(alone.Calls.Skip(2), station.Calls.Skip(2), StringComparer.Ordinal);
      Assert.DoesNotContain(station.Calls, call => call.Contains(Broken, StringComparison.Ordinal) || call.Contains(Third, StringComparison.Ordinal));
   }
}
