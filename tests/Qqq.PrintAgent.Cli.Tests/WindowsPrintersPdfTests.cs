/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Text;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Printing a staged PDF, run against a Windows and a PDF renderer that exist only in memory: the device context a
/// document is built on, the media check before it starts, where and how large each page is drawn, the copy and page
/// loops, and the cleanup on each failure. Each test asserts the calls sent and their order with their arguments, which
/// is the evidence that what reaches GDI and the spooler is what it was before the calls were moved behind
/// <see cref="IGdiPrinter"/> and <see cref="IPdfRenderer"/>. The staged files are synthetic; nothing is rendered or printed.
/// </summary>
public sealed class WindowsPrintersPdfTests
{
   private const string Queue = "Synthetic label queue";
   private const string Driver = "Synthetic label driver";
   private const string PrinterId = "72a900462ca6a6e18ecb8fb2";
   private const string LabelDrawn = "0, 0, 1200, 1800, 0, 0, 600, 900, 4 bytes, 600x-900 planes 1 bits 32";

   /// <summary>What discovery sends for the test queue when it has no machine defaults.</summary>
   private static readonly string[] Discovery = ["EnumPrintersSize(6)", "EnumPrinters(6, 136)", $"OpenPrinter({Queue})", "GetPrinterSize(0x201, 8)", "ClosePrinter(0x201)"];

   /// <summary>What discovery sends for the test queue when it has machine defaults: the level 8 read, then the resolution from a context built on them.</summary>
   private static readonly string[] DiscoveryWithDefaults =
   [
      "EnumPrintersSize(6)", "EnumPrinters(6, 136)", $"OpenPrinter({Queue})", "GetPrinterSize(0x201, 8)", "GetPrinter(0x201, 8, 232)", "ClosePrinter(0x201)",
      $"CreateDC(WINSPOOL, {Queue}, 224 bytes)", "GetDeviceCaps(0xd01, 88)", "GetDeviceCaps(0xd01, 90)", "DeleteDC(0xd01)",
   ];

   private static readonly byte[] Content = Encoding.ASCII.GetBytes("%PDF-synthetic; the fake renderer never reads this");



   /// <summary>
   /// A job without a media selection prints on the queue's current defaults (a context created with no settings), as one
   /// document named "QQQ" plus the job identifier. Each copy is every page in order; a page is taken, rendered, drawn
   /// between StartPage and EndPage and released before the next one. The receipt is the spooler's job number.
   /// </summary>
   [Fact]
   public async Task UnselectedPdfPrintsEveryPageOfEveryCopyInOneDocument()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakePdfRenderer pdf, _) = Setup();
      pdf.Pages.Add((384, 576));
      using StagedDocument staged = new(Content);

      string receipt = await printers.PrintFileAsync(staged.Job(PrinterId, "PDF", new PrintOptions(Copies: 2)), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None);

      Assert.Equal("7301", receipt);
      Assert.Equal(
      [
         .. Discovery, "LoadPdf", $"CreateDC(WINSPOOL, {Queue}, null)", "StartDoc(0xd01, QQQ job-1)",
         .. Page("0xd01", 0, "600, 900", LabelDrawn), .. Page("0xd01", 1, "600, 900", LabelDrawn),
         .. Page("0xd01", 0, "600, 900", LabelDrawn), .. Page("0xd01", 1, "600, 900", LabelDrawn),
         "EndDoc(0xd01)", "DeleteDC(0xd01)",
      ], windows.Calls, StringComparer.Ordinal);
      FakeGdiDocument document = Assert.Single(windows.GdiDocuments);
      Assert.Equal((Queue, "QQQ job-1", 7301, 4, 4), (document.Queue, document.Name, document.Job, document.PagesStarted, document.PagesEnded));
      Assert.Equal([0, 1, 0, 1], document.Drawn.Select(bits => (int)bits[0]));
      Assert.True(document.Ended);
      Assert.False(document.Aborted);
      Assert.Null(Assert.Single(windows.ContextModes));
      Assert.Equal((4, 4), (pdf.PagesTaken, pdf.PagesReleased));
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>
   /// At actual size a page is drawn at its true physical size for the context's own resolution, moved up and left by the
   /// context's unprintable margin so the page origin is the paper edge. Any scale word but FIT, in exact letter case,
   /// means actual size.
   /// </summary>
   [Theory]
   [InlineData("ACTUAL_SIZE")]
   [InlineData("fit")]
   [InlineData("SHRINK")]
   public async Task ActualSizeUsesTheContextsResolutionAndPaperEdge(string scale)
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, _, FakeQueue queue) = Setup();
      _ = queue.WithContext(812, 1218, 203, 203);
      queue.DeviceCaps[112] = 12;
      queue.DeviceCaps[113] = 9;
      using StagedDocument staged = new(Content);

      await printers.PrintFileAsync(staged.Job(PrinterId, "PDF", new PrintOptions(Scale: scale)), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None);

      Assert.Equal(Page("0xd01", 0, "600, 900", "-12, -9, 812, 1218, 0, 0, 600, 900, 4 bytes, 600x-900 planes 1 bits 32"), windows.Calls.Skip(8).Take(11), StringComparer.Ordinal);
   }



   /// <summary>
   /// FIT asks the context for its printable area, scales the page by the smaller of the two ratios (down for a page that
   /// is too large, up for one that is smaller) and centers it in that area.
   /// </summary>
   [Theory]
   [InlineData(768, 1152, "1200, 1800")]
   [InlineData(192, 288, "300, 450")]
   public async Task FitScalesToThePrintableAreaAndCenters(double width, double height, string rendered)
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakePdfRenderer pdf, FakeQueue queue) = Setup();
      pdf.Pages[0] = (width, height);
      queue.DeviceCaps[10] = 2000;
      using StagedDocument staged = new(Content);

      await printers.PrintFileAsync(staged.Job(PrinterId, "PDF", new PrintOptions(Scale: "FIT")), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None);

      string drawn = $"0, 100, 1200, 1800, 0, 0, {rendered}, 4 bytes, {rendered.Replace(", ", "x-", StringComparison.Ordinal)} planes 1 bits 32";
      Assert.Equal(Page("0xd01", 0, rendered, drawn, fit: true), windows.Calls.Skip(8).Take(13), StringComparer.Ordinal);
   }



   /// <summary>
   /// The rendered bitmap is bounded whatever the document says: at most 150 dpi equivalent, at most 5,000 pixels on the
   /// long side, and never less than one pixel (the second page here would be 0.78 of a pixel). The bitmap is drawn with
   /// the size the renderer reports, not the size asked.
   /// </summary>
   [Fact]
   public async Task RenderedBitmapIsBoundedAndDrawnAtItsReportedSize()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakePdfRenderer pdf, _) = Setup();
      pdf.Pages[0] = (8192, 4096);
      pdf.Pages.Add((0.5, 0.5));
      pdf.ReportedSize = (601, 899);
      using StagedDocument staged = new(Content);

      await printers.PrintFileAsync(staged.Job(PrinterId, "PDF"), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None);

      Assert.Equal(["RenderPage(0, 5000, 2500)", "RenderPage(1, 1, 1)"], windows.Calls.Where(call => call.StartsWith("RenderPage", StringComparison.Ordinal)), StringComparer.Ordinal);
      Assert.Equal(
      [
         "StretchDIBits(0xd01, 0, 0, 25600, 12800, 0, 0, 601, 899, 4 bytes, 601x-899 planes 1 bits 32, 0, 0xcc0020)",
         "StretchDIBits(0xd01, 0, 0, 1, 1, 0, 0, 601, 899, 4 bytes, 601x-899 planes 1 bits 32, 0, 0xcc0020)",
      ], windows.Calls.Where(call => call.StartsWith("StretchDIBits", StringComparison.Ordinal)), StringComparer.Ordinal);
   }



   /// <summary>
   /// A page too small to cover one device pixel (a hundredth of an inch square at 300 dpi comes to 0.3 of a pixel) is
   /// still sent to GDI, with a destination that has no width and no height. The fake answers 0 for such a destination,
   /// which is what StretchDIBits is documented to return when it copies no row, and the rules take 0 as a failure: the
   /// job fails with a Windows error, the document that was started is aborted and nothing stays queued. The code of that
   /// error is whatever StretchDIBits left behind and is not asserted, because the fake arranges none.
   /// </summary>
   [Fact]
   public async Task PageSmallerThanOneDevicePixelFailsTheJobAndAbortsTheDocument()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakePdfRenderer pdf, _) = Setup();
      pdf.Pages[0] = (0.1, 0.1);
      using StagedDocument staged = new(Content);

      await Assert.ThrowsAsync<Win32Exception>(() => printers.PrintFileAsync(staged.Job(PrinterId, "PDF"), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));

      Assert.Equal(
      [
         "StartPage(0xd01)", "SetStretchBltMode(0xd01, 4)", "StretchDIBits(0xd01, 0, 0, 0, 0, 0, 0, 1, 1, 4 bytes, 1x-1 planes 1 bits 32, 0, 0xcc0020)",
         "DisposePage(0)", "AbortDoc(0xd01)", "DeleteDC(0xd01)",
      ], windows.Calls.TakeLast(6), StringComparer.Ordinal);
      FakeGdiDocument document = Assert.Single(windows.GdiDocuments);
      Assert.Equal((true, false, 1, 0), (document.Aborted, document.Ended, document.PagesStarted, document.PagesEnded));
      Assert.Empty(document.Drawn);
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>
   /// A document the renderer loads with no pages in it is not refused. It is started and ended as one document that holds
   /// no page, once, however many copies were asked for, and the job gets that document's receipt; nothing is drawn and
   /// nothing is aborted. The service therefore records such a job as spooled.
   /// </summary>
   [Fact]
   public async Task PdfWithoutPagesIsStartedAndEndedAsOneEmptyDocument()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakePdfRenderer pdf, _) = Setup();
      pdf.Pages.Clear();
      using StagedDocument staged = new(Content);

      string receipt = await printers.PrintFileAsync(staged.Job(PrinterId, "PDF", new PrintOptions(Copies: 2)), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None);

      Assert.Equal("7301", receipt);
      Assert.Equal([.. Discovery, "LoadPdf", $"CreateDC(WINSPOOL, {Queue}, null)", "StartDoc(0xd01, QQQ job-1)", "EndDoc(0xd01)", "DeleteDC(0xd01)"], windows.Calls, StringComparer.Ordinal);
      FakeGdiDocument document = Assert.Single(windows.GdiDocuments);
      Assert.Equal((0, 0, true, false), (document.PagesStarted, document.PagesEnded, document.Ended, document.Aborted));
      Assert.Equal(0, pdf.PagesTaken);
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>
   /// The renderer is handed the staged file from its first byte. The file has just been read to its end to check its
   /// SHA-256, so it is put back to the start before it is handed over; the fake renderer reads the stream from where it
   /// stands when it gets it, and what it reads is the whole file.
   /// </summary>
   [Fact]
   public async Task RendererIsHandedTheStagedFileFromItsFirstByte()
   {
      (WindowsPrinters printers, _, FakePdfRenderer pdf, _) = Setup();
      using StagedDocument staged = new(Content);
      await printers.PrintFileAsync(staged.Job(PrinterId, "PDF"), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None);
      Assert.Equal(Content, pdf.Handed);
   }



   /// <summary>
   /// Each axis of a page is sized with its own resolution. On a context that reports 203 dpi across and 300 dpi down, a
   /// 4 by 6 inch page is drawn 812 pixels wide and 1,800 pixels high; with the two resolutions the wrong way round it
   /// would be 1,200 by 1,218.
   /// </summary>
   [Fact]
   public async Task PageIsSizedAcrossWithTheHorizontalResolutionAndDownWithTheVertical()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, _, FakeQueue queue) = Setup();
      _ = queue.WithContext(812, 1800, 203, 300);
      using StagedDocument staged = new(Content);

      await printers.PrintFileAsync(staged.Job(PrinterId, "PDF"), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None);

      Assert.Equal(Page("0xd01", 0, "600, 900", "0, 0, 812, 1800, 0, 0, 600, 900, 4 bytes, 600x-900 planes 1 bits 32"), windows.Calls.Skip(8).Take(11), StringComparer.Ordinal);
   }



   /// <summary>A page whose stated size is not a positive finite number is refused before it is rendered; the document that was started is aborted and the context released.</summary>
   [Theory]
   [InlineData(double.NaN, 576d)]
   [InlineData(384d, double.PositiveInfinity)]
   [InlineData(0d, 576d)]
   [InlineData(384d, -1d)]
   public async Task PageWithAnInvalidSizeAbortsTheDocument(double width, double height)
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakePdfRenderer pdf, _) = Setup();
      pdf.Pages[0] = (width, height);
      using StagedDocument staged = new(Content);

      InvalidDataException refusal = await Assert.ThrowsAsync<InvalidDataException>(() => printers.PrintFileAsync(staged.Job(PrinterId, "PDF"), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));

      Assert.Equal("PDF page dimensions are invalid.", refusal.Message);
      Assert.Equal(["StartDoc(0xd01, QQQ job-1)", "GetPage(0)", "DisposePage(0)", "AbortDoc(0xd01)", "DeleteDC(0xd01)"], windows.Calls.TakeLast(5), StringComparer.Ordinal);
      FakeGdiDocument document = Assert.Single(windows.GdiDocuments);
      Assert.True(document.Aborted);
      Assert.Equal(0, document.PagesStarted);
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>A page too large to express in device pixels is an overflow, not a wrapped size: the document is aborted before any page is started.</summary>
   [Fact]
   public async Task PageTooLargeForTheDeviceAbortsTheDocument()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakePdfRenderer pdf, _) = Setup();
      pdf.Pages[0] = (1_073_741_824, 576);
      using StagedDocument staged = new(Content);
      await Assert.ThrowsAsync<OverflowException>(() => printers.PrintFileAsync(staged.Job(PrinterId, "PDF"), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));
      Assert.Equal(["RenderPage(0, 5000, 1)", "GetDeviceCaps(0xd01, 88)", "DisposePage(0)", "AbortDoc(0xd01)", "DeleteDC(0xd01)"], windows.Calls.TakeLast(5), StringComparer.Ordinal);
      Assert.True(Assert.Single(windows.GdiDocuments).Aborted);
   }



   /// <summary>
   /// A job that selected the queue's default media: the selection is read again, a job-only copy of the settings is merged
   /// by the driver and resolved again, the context is created from that copy (never from null), and StartDoc is sent only
   /// after the context reported the selected resolution and physical page.
   /// </summary>
   [Fact]
   public async Task SelectedMediaIsCheckedOnTheContextBeforeTheDocumentStarts()
   {
      byte[] defaults = SyntheticDevMode.Label().WithFields(SyntheticDevMode.Orientation | SyntheticDevMode.PaperDimensions | SyntheticDevMode.Scale | SyntheticDevMode.Copies).WithShort(84, 50).WithShort(86, 3);
      (WindowsPrinters printers, FakeWindowsPrinting windows, _, _) = Setup(defaults);
      using StagedDocument staged = new(Content);

      string receipt = await printers.PrintFileAsync(staged.Job(PrinterId, "PDF", Selection(defaults)), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None);

      Assert.Equal("7301", receipt);
      Assert.Equal(
      [
         .. DiscoveryWithDefaults, "LoadPdf",
         $"OpenPrinter({Queue})", "GetPrinterSize(0x202, 8)", "GetPrinter(0x202, 8, 232)", "ClosePrinter(0x202)",
         $"CreateDC(WINSPOOL, {Queue}, 224 bytes)", "GetDeviceCaps(0xd02, 88)", "GetDeviceCaps(0xd02, 90)", "DeleteDC(0xd02)",
         $"OpenPrinter({Queue})", $"DocumentPropertiesSize(0x203, {Queue})", $"DocumentProperties(0x203, {Queue}, 224 bytes, 224 bytes, 10)", "ClosePrinter(0x203)",
         $"CreateDC(WINSPOOL, {Queue}, 224 bytes)", "GetDeviceCaps(0xd03, 88)", "GetDeviceCaps(0xd03, 90)", "DeleteDC(0xd03)",
         $"CreateDC(WINSPOOL, {Queue}, 224 bytes)", "GetDeviceCaps(0xd04, 88)", "GetDeviceCaps(0xd04, 90)", "GetDeviceCaps(0xd04, 110)", "GetDeviceCaps(0xd04, 111)",
         "StartDoc(0xd04, QQQ job-1)", .. Page("0xd04", 0, "600, 900", LabelDrawn), "EndDoc(0xd04)", "DeleteDC(0xd04)",
      ], windows.Calls, StringComparer.Ordinal);
      byte[] used = Assert.IsType<byte[]>(windows.ContextModes[^1]);
      Assert.Equal((100, 1), (SyntheticDevMode.Short(used, 84), SyntheticDevMode.Short(used, 86)));
      Assert.Equal(defaults, windows.ContextModes[0]);
      Assert.True(Assert.Single(windows.GdiDocuments).Ended);
      Assert.Equal(0, windows.OpenPrinters);
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>When the context built from the job's settings does not report the selected page, the job is refused as a media mismatch: StartDoc is never sent, nothing is aborted and the context is released.</summary>
   [Fact]
   public async Task SubstitutedContextNeverStartsADocument()
   {
      byte[] defaults = SyntheticDevMode.Label();
      (WindowsPrinters printers, FakeWindowsPrinting windows, _, FakeQueue queue) = Setup(defaults);
      queue.DeviceCaps[111] = 600;
      using StagedDocument staged = new(Content);

      PrinterMediaMismatchException refusal = await Assert.ThrowsAsync<PrinterMediaMismatchException>(() => printers.PrintFileAsync(staged.Job(PrinterId, "PDF", Selection(defaults)), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));

      Assert.Equal("The printer device context does not match the selected media dimensions and resolution.", refusal.Message);
      Assert.Equal(["GetDeviceCaps(0xd04, 88)", "GetDeviceCaps(0xd04, 90)", "GetDeviceCaps(0xd04, 110)", "GetDeviceCaps(0xd04, 111)", "DeleteDC(0xd04)"], windows.Calls.TakeLast(5), StringComparer.Ordinal);
      Assert.Empty(windows.GdiDocuments);
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>
   /// Explicit job paper: the machine defaults are read fresh, a copy carrying the requested size is merged by the driver,
   /// and the context made from it must report that physical size at its own resolution before StartDoc is sent. A driver
   /// that keeps its 4 by 6 inch default is refused before StartDoc.
   /// </summary>
   [Theory]
   [InlineData(300, true)]
   [InlineData(1800, false)]
   public async Task JobPaperMustBeWhatTheContextReports(int contextHeight, bool printed)
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakePdfRenderer pdf, FakeQueue queue) = Setup(SyntheticDevMode.Label());
      _ = queue.WithContext(1200, contextHeight);
      pdf.Pages[0] = (384, 96);
      using StagedDocument staged = new(Content);
      PrintJob job = staged.Job(PrinterId, "PDF", new PrintOptions(PaperWidthMm: 101.6, PaperHeightMm: 25.4));
      string[] prepared =
      [
         .. DiscoveryWithDefaults, "LoadPdf",
         $"OpenPrinter({Queue})", "GetPrinterSize(0x202, 8)", "GetPrinter(0x202, 8, 232)", "ClosePrinter(0x202)",
         $"OpenPrinter({Queue})", $"DocumentPropertiesSize(0x203, {Queue})", $"DocumentProperties(0x203, {Queue}, 224 bytes, 224 bytes, 10)", "ClosePrinter(0x203)",
         $"CreateDC(WINSPOOL, {Queue}, 224 bytes)", "GetDeviceCaps(0xd02, 88)", "GetDeviceCaps(0xd02, 90)",
         "GetDeviceCaps(0xd02, 88)", "GetDeviceCaps(0xd02, 90)", "GetDeviceCaps(0xd02, 110)", "GetDeviceCaps(0xd02, 111)",
      ];

      if(printed)
      {
         Assert.Equal("7301", await printers.PrintFileAsync(job, staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));
         Assert.Equal([.. prepared, "StartDoc(0xd02, QQQ job-1)", .. Page("0xd02", 0, "600, 150", "0, 0, 1200, 300, 0, 0, 600, 150, 4 bytes, 600x-150 planes 1 bits 32"), "EndDoc(0xd02)", "DeleteDC(0xd02)"], windows.Calls, StringComparer.Ordinal);
      }
      else
      {
         await Assert.ThrowsAsync<PrinterMediaMismatchException>(() => printers.PrintFileAsync(job, staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));
         Assert.Equal([.. prepared, "DeleteDC(0xd02)"], windows.Calls, StringComparer.Ordinal);
         Assert.Empty(windows.GdiDocuments);
      }
      byte[] used = Assert.IsType<byte[]>(windows.ContextModes[^1]);
      Assert.Equal((256, 254, 1016), (SyntheticDevMode.Short(used, 78), SyntheticDevMode.Short(used, 80), SyntheticDevMode.Short(used, 82)));
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>
   /// The media check before StartDoc is made per axis. The selection is 4 by 6 inches at 203 dpi across and 300 dpi down,
   /// taken from settings that state both resolutions, so the context is asked only at this check. A context that reports
   /// 203 across, 300 down and a physical page of 812 by 1,800 pixels starts the document. A context that reports the same
   /// two resolutions the other way round (with the page that would match them that way) is a mismatch and starts nothing.
   /// </summary>
   [Theory]
   [InlineData(203, 300, 812, 1800, true)]
   [InlineData(300, 203, 1200, 1218, false)]
   public async Task SelectedMediaIsCheckedWithEachAxisAgainstItsOwnResolution(int dpiX, int dpiY, int widthPixels, int heightPixels, bool printed)
   {
      byte[] defaults = SyntheticDevMode.Label().WithFields(SyntheticDevMode.Orientation | SyntheticDevMode.PaperDimensions | SyntheticDevMode.Resolution).WithShort(90, 203).WithShort(96, 300);
      (WindowsPrinters printers, FakeWindowsPrinting windows, _, FakeQueue queue) = Setup(defaults);
      _ = queue.WithContext(widthPixels, heightPixels, dpiX, dpiY);
      using StagedDocument staged = new(Content);
      PrintJob job = staged.Job(PrinterId, "PDF", Selection(defaults, 203, 300));

      if(printed)
      {
         Assert.Equal("7301", await printers.PrintFileAsync(job, staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));
         Assert.Equal(
         [
            $"CreateDC(WINSPOOL, {Queue}, 224 bytes)", "GetDeviceCaps(0xd01, 88)", "GetDeviceCaps(0xd01, 90)", "GetDeviceCaps(0xd01, 110)", "GetDeviceCaps(0xd01, 111)", "StartDoc(0xd01, QQQ job-1)",
         ], windows.Calls.SkipWhile(call => !call.StartsWith("CreateDC", StringComparison.Ordinal)).Take(6), StringComparer.Ordinal);
         Assert.Contains("StretchDIBits(0xd01, 0, 0, 812, 1800, 0, 0, 600, 900, 4 bytes, 600x-900 planes 1 bits 32, 0, 0xcc0020)", windows.Calls, StringComparer.Ordinal);
         Assert.True(Assert.Single(windows.GdiDocuments).Ended);
      }
      else
      {
         PrinterMediaMismatchException refusal = await Assert.ThrowsAsync<PrinterMediaMismatchException>(() => printers.PrintFileAsync(job, staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));
         Assert.Equal("The printer device context does not match the selected media dimensions and resolution.", refusal.Message);
         Assert.Equal([$"CreateDC(WINSPOOL, {Queue}, 224 bytes)", "GetDeviceCaps(0xd01, 88)", "GetDeviceCaps(0xd01, 90)", "DeleteDC(0xd01)"], windows.Calls.TakeLast(4), StringComparer.Ordinal);
         Assert.Empty(windows.GdiDocuments);
      }
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>
   /// Job paper is checked per axis as well: 101.6 by 25.4 mm on a context that reports 203 dpi across and 300 dpi down
   /// must be a physical page of 812 by 300 pixels, and the page is then drawn at that size.
   /// </summary>
   [Fact]
   public async Task JobPaperIsCheckedWithEachAxisAgainstItsOwnResolution()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakePdfRenderer pdf, FakeQueue queue) = Setup(SyntheticDevMode.Label());
      _ = queue.WithContext(812, 300, 203, 300);
      pdf.Pages[0] = (384, 96);
      using StagedDocument staged = new(Content);

      string receipt = await printers.PrintFileAsync(staged.Job(PrinterId, "PDF", new PrintOptions(PaperWidthMm: 101.6, PaperHeightMm: 25.4)), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None);

      Assert.Equal("7301", receipt);
      Assert.Contains("StretchDIBits(0xd02, 0, 0, 812, 300, 0, 0, 600, 150, 4 bytes, 600x-150 planes 1 bits 32, 0, 0xcc0020)", windows.Calls, StringComparer.Ordinal);
      Assert.True(Assert.Single(windows.GdiDocuments).Ended);
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>A context Windows will not create fails with its Windows error before anything is started; there is nothing to release.</summary>
   [Fact]
   public async Task RefusedContextStartsNothing()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, _, FakeQueue queue) = Setup();
      queue.CreateContextError = 1801;
      using StagedDocument staged = new(Content);
      Win32Exception failure = await Assert.ThrowsAsync<Win32Exception>(() => printers.PrintFileAsync(staged.Job(PrinterId, "PDF"), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));
      Assert.Equal(1801, failure.NativeErrorCode);
      Assert.Equal([.. Discovery, "LoadPdf", $"CreateDC(WINSPOOL, {Queue}, null)"], windows.Calls, StringComparer.Ordinal);
      Assert.Empty(windows.GdiDocuments);
   }



   /// <summary>A document Windows refuses to start (a job number of zero or less) fails with its Windows error; there is nothing to abort, and the context is released.</summary>
   [Theory]
   [InlineData(0)]
   [InlineData(-1)]
   public async Task RefusedDocumentIsNotAbortedAndTheContextIsReleased(int answer)
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, _, _) = Setup();
      windows.Fail("StartDoc", 1905, result: answer);
      using StagedDocument staged = new(Content);
      Win32Exception failure = await Assert.ThrowsAsync<Win32Exception>(() => printers.PrintFileAsync(staged.Job(PrinterId, "PDF"), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));
      Assert.Equal(1905, failure.NativeErrorCode);
      Assert.Equal([$"CreateDC(WINSPOOL, {Queue}, null)", "StartDoc(0xd01, QQQ job-1)", "DeleteDC(0xd01)"], windows.Calls.TakeLast(3), StringComparer.Ordinal);
      Assert.Empty(windows.GdiDocuments);
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>
   /// Any page call or the end of the document that Windows refuses keeps its Windows error; the document that was started
   /// is aborted, so nothing partial is printed, and the context is released. A failure on a later page aborts the pages
   /// before it too.
   /// </summary>
   [Theory]
   [InlineData("StartPage", 0, 1)]
   [InlineData("StartPage", -1, 1)]
   [InlineData("StretchDIBits", 0, 1)]
   [InlineData("StretchDIBits", -1, 1)]
   [InlineData("EndPage", 0, 1)]
   [InlineData("EndPage", -1, 2)]
   [InlineData("EndDoc", 0, 1)]
   [InlineData("EndDoc", -1, 1)]
   public async Task FailureAfterTheDocumentStartedAbortsIt(string call, int answer, int occurrence)
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakePdfRenderer pdf, _) = Setup();
      pdf.Pages.Add((384, 576));
      windows.Fail(call, 29, occurrence, answer);
      using StagedDocument staged = new(Content);

      Win32Exception failure = await Assert.ThrowsAsync<Win32Exception>(() => printers.PrintFileAsync(staged.Job(PrinterId, "PDF"), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));

      Assert.Equal(29, failure.NativeErrorCode);
      Assert.Equal(["AbortDoc(0xd01)", "DeleteDC(0xd01)"], windows.Calls.TakeLast(2), StringComparer.Ordinal);
      Assert.StartsWith(call, windows.Calls[^(string.Equals(call, "EndDoc", StringComparison.Ordinal) ? 3 : 4)], StringComparison.Ordinal);
      FakeGdiDocument document = Assert.Single(windows.GdiDocuments);
      Assert.True(document.Aborted);
      Assert.False(document.Ended);
      Assert.Equal(pdf.PagesTaken, pdf.PagesReleased);
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>
   /// A refused stretch mode aborts the document like any other page call, but the Windows error it is reported with is
   /// not its own. The import of SetStretchBltMode is declared without SetLastError, so the failure carries whatever the
   /// last call that records an error left, which here is the StartPage that succeeded just before; the fake's marker
   /// stands for that, and the error the test arranges for the stretch mode is never seen. The code did the same before
   /// the calls were moved behind <see cref="IGdiPrinter"/>.
   /// </summary>
   [Fact]
   public async Task RefusedStretchModeAbortsTheDocumentWithAnErrorThatIsNotItsOwn()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakePdfRenderer pdf, _) = Setup();
      windows.Fail("SetStretchBltMode", 29);
      using StagedDocument staged = new(Content);

      Win32Exception failure = await Assert.ThrowsAsync<Win32Exception>(() => printers.PrintFileAsync(staged.Job(PrinterId, "PDF"), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));

      Assert.Equal(StaleLastError.Code, failure.NativeErrorCode);
      Assert.Equal(["StartPage(0xd01)", "SetStretchBltMode(0xd01, 4)", "DisposePage(0)", "AbortDoc(0xd01)", "DeleteDC(0xd01)"], windows.Calls.TakeLast(5), StringComparer.Ordinal);
      FakeGdiDocument document = Assert.Single(windows.GdiDocuments);
      Assert.Equal((true, false, 1, 0), (document.Aborted, document.Ended, document.PagesStarted, document.PagesEnded));
      Assert.Equal(pdf.PagesTaken, pdf.PagesReleased);
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>Cancellation is checked before every page: a job cancelled after its first page is aborted and never ended.</summary>
   [Fact]
   public async Task CancellationBetweenPagesAbortsTheDocument()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakePdfRenderer pdf, _) = Setup();
      pdf.Pages.Add((384, 576));
      using CancellationTokenSource cancel = new();
      pdf.OnGetPage = _ => cancel.Cancel();
      using StagedDocument staged = new(Content);

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => printers.PrintFileAsync(staged.Job(PrinterId, "PDF"), staged.Path, Timeout.InfiniteTimeSpan, cancel.Token));

      Assert.Equal(["EndPage(0xd01)", "DisposePage(0)", "AbortDoc(0xd01)", "DeleteDC(0xd01)"], windows.Calls.TakeLast(4), StringComparer.Ordinal);
      FakeGdiDocument document = Assert.Single(windows.GdiDocuments);
      Assert.Equal((1, 1, true, false), (document.PagesStarted, document.PagesEnded, document.Aborted, document.Ended));
   }



   /// <summary>A file the renderer cannot load fails with the renderer's own error before any context is created.</summary>
   [Fact]
   public async Task UnreadablePdfCreatesNoContext()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakePdfRenderer pdf, _) = Setup();
      pdf.LoadFailure = new InvalidDataException("Synthetic: not a PDF.");
      using StagedDocument staged = new(Content);
      InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() => printers.PrintFileAsync(staged.Job(PrinterId, "PDF"), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));
      Assert.Same(pdf.LoadFailure, failure);
      Assert.Equal([.. Discovery, "LoadPdf"], windows.Calls, StringComparer.Ordinal);
   }



   /// <summary>Builds the rules over a fake Windows with the one label queue (a 4 by 6 inch, 300 dpi context) and a fake renderer holding one 4 by 6 inch page.</summary>
   private static (WindowsPrinters Printers, FakeWindowsPrinting Windows, FakePdfRenderer Pdf, FakeQueue Queue) Setup(byte[]? defaults = null)
   {
      FakeWindowsPrinting windows = new();
      FakePdfRenderer pdf = new(windows.Calls);
      FakeQueue queue = windows.Add(new FakeQueue(Queue, Driver, "USB001") { MachineDefaults = defaults }.WithContext(1200, 1800));
      return (new WindowsPrinters(windows, windows, windows, pdf), windows, pdf, queue);
   }



   /// <summary>Options that select the profile discovery reports for the test queue with the given machine defaults, at 300 dpi both ways unless a test says otherwise.</summary>
   private static PrintOptions Selection(byte[] defaults, int dpiX = 300, int dpiY = 300)
   {
      PrinterMediaProfile profile = WindowsPrinters.BuildMediaProfile(PrinterId, Driver, defaults, 101600, 152400, dpiX, dpiY)!;
      return new PrintOptions(MediaProfile: new PrinterMediaSelection(profile.Id, profile.Revision));
   }



   /// <summary>
   /// The calls one page costs, in order: take the page, render it, read the context's resolution and margin (and its
   /// printable area for FIT), start the page, set the stretch mode to 4, draw the whole bitmap with a plain copy, end the
   /// page and release it.
   /// </summary>
   private static string[] Page(string dc, int index, string rendered, string drawn, bool fit = false) =>
   [
      $"GetPage({index})", $"RenderPage({index}, {rendered})",
      $"GetDeviceCaps({dc}, 88)", $"GetDeviceCaps({dc}, 90)", $"GetDeviceCaps({dc}, 112)", $"GetDeviceCaps({dc}, 113)",
      .. fit ? new[] { $"GetDeviceCaps({dc}, 8)", $"GetDeviceCaps({dc}, 10)" } : [],
      $"StartPage({dc})", $"SetStretchBltMode({dc}, 4)", $"StretchDIBits({dc}, {drawn}, 0, 0xcc0020)", $"EndPage({dc})", $"DisposePage({index})",
   ];
}
