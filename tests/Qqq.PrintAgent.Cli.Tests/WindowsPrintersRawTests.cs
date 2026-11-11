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
/// Printing a staged file, run against a Windows that exists only in memory: the checks made before anything is
/// spooled, and the RAW ZPL document from its first call to its last. Each test asserts the calls sent and their order
/// and, where a document was started, what the fake spooler was handed. That is the evidence that what reaches the
/// spooler (the document name, the RAW data type, the bytes, the copies, and the cleanup on each failure) is what it was
/// before the calls were moved behind <see cref="ISpooler"/>. The staged files are synthetic and nothing is printed.
/// </summary>
public sealed class WindowsPrintersRawTests
{
   private const string Queue = "Synthetic Zebra queue";
   private const string PrinterId = "6119358ca3e4aafc30d6a3da";

   /// <summary>What discovery sends for the one test queue when it has no machine defaults; every print starts with it.</summary>
   private static readonly string[] Discovery = ["EnumPrintersSize(6)", "EnumPrinters(6, 136)", $"OpenPrinter({Queue})", "GetPrinterSize(0x201, 8)", "ClosePrinter(0x201)"];

   private static readonly byte[] Label = Encoding.ASCII.GetBytes("^XA^FO50,50^FDsynthetic^FS^XZ");



   /// <summary>
   /// A ZPL file becomes one spool document named "QQQ" plus the job identifier with data type RAW: open the queue, start
   /// the document, write the bytes, end the document, close the queue. The receipt is the spooler's job number.
   /// </summary>
   [Fact]
   public async Task ZplFileIsSpooledAsOneRawDocument()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, _) = Setup();
      using StagedDocument staged = new(Label);
      PrintJob job = staged.Job(PrinterId, "ZPL") with { ContentSha256 = staged.Job(PrinterId, "ZPL").ContentSha256.ToLowerInvariant() };

      string receipt = await printers.PrintFileAsync(job, staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None);

      Assert.Equal("4101", receipt);
      Assert.Equal(
      [
         .. Discovery,
         $"OpenPrinter({Queue})", "StartDocPrinter(0x202, QQQ job-1, RAW)", $"WritePrinter(0x202, {Label.Length} bytes, {Label.Length})", "EndDocPrinter(0x202)", "ClosePrinter(0x202)",
      ], windows.Calls, StringComparer.Ordinal);
      FakeSpoolDocument document = Assert.Single(windows.RawDocuments);
      Assert.Equal((Queue, "QQQ job-1", "RAW", 4101u), (document.Queue, document.Name, document.DataType, document.Job));
      Assert.Equal(Label, document.Bytes.ToArray());
      Assert.True(document.Ended);
      Assert.False(document.Aborted);
      Assert.Equal(0, windows.OpenPrinters);
   }



   /// <summary>Every copy is the whole file again, written in pieces of at most 64 KiB inside the same document: three copies of 150,000 bytes are nine writes and 450,000 bytes.</summary>
   [Fact]
   public async Task EveryCopyIsTheWholeFileIn64KibPieces()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, _) = Setup();
      byte[] content = Pattern(150_000);
      using StagedDocument staged = new(content);

      await printers.PrintFileAsync(staged.Job(PrinterId, "ZPL", new PrintOptions(Copies: 3)), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None);

      string[] copy = ["WritePrinter(0x202, 65536 bytes, 65536)", "WritePrinter(0x202, 65536 bytes, 65536)", "WritePrinter(0x202, 18928 bytes, 18928)"];
      Assert.Equal([.. Discovery, $"OpenPrinter({Queue})", "StartDocPrinter(0x202, QQQ job-1, RAW)", .. copy, .. copy, .. copy, "EndDocPrinter(0x202)", "ClosePrinter(0x202)"], windows.Calls, StringComparer.Ordinal);
      FakeSpoolDocument document = Assert.Single(windows.RawDocuments);
      Assert.Equal([.. content, .. content, .. content], document.Bytes.ToArray());
      Assert.True(document.Ended);
   }



   /// <summary>When the spooler takes fewer bytes than it was offered, the rest is offered again from where it stopped, so nothing is lost or repeated.</summary>
   [Fact]
   public async Task PartialWriteIsContinuedFromWhereTheSpoolerStopped()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, _) = Setup();
      windows.WriteLimit = 1000;
      byte[] content = Pattern(2500);
      using StagedDocument staged = new(content);

      await printers.PrintFileAsync(staged.Job(PrinterId, "ZPL"), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None);

      Assert.Equal(["WritePrinter(0x202, 2500 bytes, 2500)", "WritePrinter(0x202, 1500 bytes, 1500)", "WritePrinter(0x202, 500 bytes, 500)"], windows.Calls.Where(call => call.StartsWith("WritePrinter", StringComparison.Ordinal)), StringComparer.Ordinal);
      Assert.Equal(content, Assert.Single(windows.RawDocuments).Bytes.ToArray());
   }



   /// <summary>The largest copy count the adapter accepts (32,767) is printed; the document holds that many copies.</summary>
   [Fact]
   public async Task LargestCopyCountIsAccepted()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, _) = Setup();
      using StagedDocument staged = new([0x5e]);
      await printers.PrintFileAsync(staged.Job(PrinterId, "ZPL", new PrintOptions(Copies: 32767)), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None);
      Assert.Equal(32767, Assert.Single(windows.RawDocuments).Bytes.Length);
   }



   /// <summary>A written count that cannot be true (none, negative, or more than was offered) stops the document: it is aborted, the queue is closed and nothing is ended.</summary>
   [Theory]
   [InlineData(0)]
   [InlineData(-1)]
   [InlineData(1)]
   public async Task ImpossibleWrittenCountAbortsTheDocument(int excess)
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, _) = Setup();
      windows.WrittenAnswer = taken => excess <= 0 ? excess : taken + excess;
      using StagedDocument staged = new(Label);

      IOException failure = await Assert.ThrowsAsync<IOException>(() => printers.PrintFileAsync(staged.Job(PrinterId, "ZPL"), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));

      Assert.Equal("Printer returned an invalid byte count.", failure.Message);
      Assert.Equal([$"WritePrinter(0x202, {Label.Length} bytes, {Label.Length})", "AbortPrinter(0x202)", "ClosePrinter(0x202)"], windows.Calls.TakeLast(3), StringComparer.Ordinal);
      FakeSpoolDocument document = Assert.Single(windows.RawDocuments);
      Assert.True(document.Aborted);
      Assert.False(document.Ended);
      Assert.Equal(0, windows.OpenPrinters);
   }



   /// <summary>A queue that cannot be opened for the document fails with its Windows error; no document is started and there is no handle to close.</summary>
   [Fact]
   public async Task QueueThatCannotBeOpenedStartsNoDocument()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, _) = Setup();
      windows.Fail("OpenPrinter", 5, occurrence: 2);
      using StagedDocument staged = new(Label);
      Win32Exception failure = await Assert.ThrowsAsync<Win32Exception>(() => printers.PrintFileAsync(staged.Job(PrinterId, "ZPL"), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));
      Assert.Equal(5, failure.NativeErrorCode);
      Assert.Equal([.. Discovery, $"OpenPrinter({Queue})"], windows.Calls, StringComparer.Ordinal);
      Assert.Empty(windows.RawDocuments);
   }



   /// <summary>A document Windows refuses to start fails with its Windows error; there is nothing to abort, and the queue is closed.</summary>
   [Fact]
   public async Task RefusedDocumentIsNotAbortedAndTheQueueIsClosed()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, _) = Setup();
      windows.Fail("StartDocPrinter", 1905);
      using StagedDocument staged = new(Label);
      Win32Exception failure = await Assert.ThrowsAsync<Win32Exception>(() => printers.PrintFileAsync(staged.Job(PrinterId, "ZPL"), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));
      Assert.Equal(1905, failure.NativeErrorCode);
      Assert.Equal([.. Discovery, $"OpenPrinter({Queue})", "StartDocPrinter(0x202, QQQ job-1, RAW)", "ClosePrinter(0x202)"], windows.Calls, StringComparer.Ordinal);
      Assert.Empty(windows.RawDocuments);
      Assert.Equal(0, windows.OpenPrinters);
   }



   /// <summary>A write or the end of the document that Windows refuses keeps its Windows error; the started document is aborted, so no partial label stays queued, and the queue is closed.</summary>
   [Theory]
   [InlineData("WritePrinter", 2)]
   [InlineData("EndDocPrinter", 3)]
   public async Task FailureAfterTheDocumentStartedAbortsIt(string call, int callsBeforeTheAbort)
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, _) = Setup();
      windows.Fail(call, 29);
      using StagedDocument staged = new(Label);

      Win32Exception failure = await Assert.ThrowsAsync<Win32Exception>(() => printers.PrintFileAsync(staged.Job(PrinterId, "ZPL"), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));

      Assert.Equal(29, failure.NativeErrorCode);
      string[] document = ["StartDocPrinter(0x202, QQQ job-1, RAW)", $"WritePrinter(0x202, {Label.Length} bytes, {Label.Length})", "EndDocPrinter(0x202)"];
      Assert.Equal([.. Discovery, $"OpenPrinter({Queue})", .. document.Take(callsBeforeTheAbort), "AbortPrinter(0x202)", "ClosePrinter(0x202)"], windows.Calls, StringComparer.Ordinal);
      FakeSpoolDocument started = Assert.Single(windows.RawDocuments);
      Assert.True(started.Aborted);
      Assert.False(started.Ended);
      Assert.Equal(0, windows.OpenPrinters);
   }



   /// <summary>Cancellation is checked before every write: a job cancelled after its first piece is aborted and never ended.</summary>
   [Fact]
   public async Task CancellationBetweenWritesAbortsTheDocument()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, _) = Setup();
      using CancellationTokenSource cancel = new();
      windows.WrittenAnswer = taken =>
      {
         cancel.Cancel();
         return taken;
      };
      using StagedDocument staged = new(Pattern(150_000));

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => printers.PrintFileAsync(staged.Job(PrinterId, "ZPL"), staged.Path, Timeout.InfiniteTimeSpan, cancel.Token));

      Assert.Equal(["StartDocPrinter(0x202, QQQ job-1, RAW)", "WritePrinter(0x202, 65536 bytes, 65536)", "AbortPrinter(0x202)", "ClosePrinter(0x202)"], windows.Calls.TakeLast(4), StringComparer.Ordinal);
      Assert.True(Assert.Single(windows.RawDocuments).Aborted);
   }



   /// <summary>
   /// A ZPL job that selected the queue's default media has that selection read again after the queue is opened and before
   /// the document is started. A selection that is still current prints; one that changed is refused as a media mismatch
   /// with no document started and the queue closed.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task SelectedMediaIsReadAgainJustBeforeTheDocumentStarts(bool current)
   {
      byte[] defaults = SyntheticDevMode.Label();
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakeQueue queue) = Setup();
      queue.MachineDefaults = defaults;
      _ = queue.WithContext(1200, 1800);
      PrinterMediaProfile profile = WindowsPrinters.BuildMediaProfile(PrinterId, queue.Driver!, current ? defaults : [.. defaults[..^1], 0x00], 101600, 152400, 300, 300)!;
      using StagedDocument staged = new(Label);
      PrintJob job = staged.Job(PrinterId, "ZPL", new PrintOptions(MediaProfile: new PrinterMediaSelection(profile.Id, profile.Revision)));
      string[] recheck =
      [
         $"OpenPrinter({Queue})", "GetPrinterSize(0x203, 8)", "GetPrinter(0x203, 8, 232)", "ClosePrinter(0x203)",
         $"CreateDC(WINSPOOL, {Queue}, 224 bytes)", "GetDeviceCaps(0xd02, 88)", "GetDeviceCaps(0xd02, 90)", "DeleteDC(0xd02)",
      ];

      if(current)
      {
         Assert.Equal("4101", await printers.PrintFileAsync(job, staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));
         Assert.Equal([$"OpenPrinter({Queue})", .. recheck, "StartDocPrinter(0x202, QQQ job-1, RAW)", $"WritePrinter(0x202, {Label.Length} bytes, {Label.Length})", "EndDocPrinter(0x202)", "ClosePrinter(0x202)"], windows.Calls.Skip(10), StringComparer.Ordinal);
      }
      else
      {
         await Assert.ThrowsAsync<PrinterMediaMismatchException>(() => printers.PrintFileAsync(job, staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));
         Assert.Equal([$"OpenPrinter({Queue})", .. recheck, "ClosePrinter(0x202)"], windows.Calls.Skip(10), StringComparer.Ordinal);
         Assert.Empty(windows.RawDocuments);
      }
      Assert.Equal(0, windows.OpenPrinters);
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>A path that is not absolute, or a file whose length or SHA-256 is not the job's, is refused before Windows is asked anything, discovery included.</summary>
   [Theory]
   [InlineData("relative", "The worker requires a regular, absolute staging file.")]
   [InlineData("length", "Staged document integrity check failed.")]
   [InlineData("hash", "Staged document integrity check failed.")]
   public async Task UnverifiedFileIsRefusedBeforeWindowsIsAsked(string fault, string message)
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, _) = Setup();
      using StagedDocument staged = new(Label);
      PrintJob job = staged.Job(PrinterId, "ZPL");
      string path = staged.Path;
      switch(fault)
      {
         case "relative":
            path = Path.GetFileName(staged.Path);
            break;
         case "length":
            job = job with { ContentLength = Label.Length + 1 };
            break;
         default:
            job = job with { ContentSha256 = new string('0', 64) };
            break;
      }
      InvalidDataException refusal = await Assert.ThrowsAsync<InvalidDataException>(() => printers.PrintFileAsync(job, path, Timeout.InfiniteTimeSpan, CancellationToken.None));
      Assert.Equal(message, refusal.Message);
      Assert.Empty(windows.Calls);
   }



   /// <summary>
   /// After discovery, a job is refused without a document when its copy count is outside 1 to 32,767, when the queue does
   /// not offer its format (ZPL on a queue that is not a Zebra, any format on a queue that needs a person, a format in the
   /// wrong letter case) or when it asks for job paper with ZPL, which carries its own dimensions.
   /// </summary>
   [Theory]
   [InlineData("no copies", "The queue or print options are unsupported.")]
   [InlineData("negative copies", "The queue or print options are unsupported.")]
   [InlineData("too many copies", "The queue or print options are unsupported.")]
   [InlineData("format not offered", "The queue or print options are unsupported.")]
   [InlineData("format in lower case", "The queue or print options are unsupported.")]
   [InlineData("queue needs a person", "The queue or print options are unsupported.")]
   [InlineData("job paper with ZPL", "Job paper dimensions apply to PDF documents only; ZPL carries its own dimensions.")]
   public async Task UnsupportedJobIsRefusedAfterDiscoveryWithoutADocument(string fault, string message)
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakeQueue queue) = Setup();
      using StagedDocument staged = new(Label);
      PrintJob job = staged.Job(PrinterId, "ZPL");
      switch(fault)
      {
         case "no copies":
            job = job with { Options = new PrintOptions(Copies: 0) };
            break;
         case "negative copies":
            job = job with { Options = new PrintOptions(Copies: -1) };
            break;
         case "too many copies":
            job = job with { Options = new PrintOptions(Copies: 32768) };
            break;
         case "format not offered":
            windows.Queues[0] = new FakeQueue("Synthetic laser queue", "Synthetic laser driver");
            job = job with { PrinterId = "f00632792c722747d33552e2" };
            break;
         case "format in lower case":
            job = job with { Format = "zpl" };
            break;
         case "queue needs a person":
            queue.Port = "FILE:";
            break;
         default:
            job = job with { Options = new PrintOptions(PaperWidthMm: 101.6, PaperHeightMm: 25.4) };
            break;
      }
      InvalidDataException refusal = await Assert.ThrowsAsync<InvalidDataException>(() => printers.PrintFileAsync(job, staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));
      Assert.Equal(message, refusal.Message);
      Assert.Equal("EnumPrinters(6, 136)", windows.Calls[1]);
      Assert.DoesNotContain(windows.Calls, call => call.StartsWith("StartDoc", StringComparison.Ordinal));
      Assert.Empty(windows.RawDocuments);
      Assert.Empty(windows.GdiDocuments);
      Assert.Equal(0, windows.OpenPrinters);
   }



   /// <summary>
   /// A job for a printer that is not installed any more fails once the queues were listed and starts nothing. The one
   /// queue that is installed is not the job's own, so it is not opened and its settings are not read.
   /// </summary>
   [Fact]
   public async Task JobForAPrinterThatIsGoneStartsNothing()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, _) = Setup();
      using StagedDocument staged = new(Label);
      await Assert.ThrowsAsync<InvalidOperationException>(() => printers.PrintFileAsync(staged.Job("000000000000000000000000", "ZPL"), staged.Path, Timeout.InfiniteTimeSpan, CancellationToken.None));
      Assert.Equal(Discovery.Take(2), windows.Calls, StringComparer.Ordinal);
      Assert.Empty(windows.RawDocuments);
   }



   /// <summary>Builds the rules over a fake Windows that has one Zebra label queue without machine defaults.</summary>
   private static (WindowsPrinters Printers, FakeWindowsPrinting Windows, FakeQueue Queue) Setup()
   {
      FakeWindowsPrinting windows = new();
      FakeQueue queue = windows.Add(new FakeQueue(Queue, "ZDesigner ZT411-300dpi ZPL", "USB001"));
      return (new WindowsPrinters(windows, windows, windows, new FakePdfRenderer(windows.Calls)), windows, queue);
   }



   /// <summary>Synthetic bytes of the given length that do not repeat at the 64 KiB piece size, so a misplaced piece would show.</summary>
   private static byte[] Pattern(int length)
   {
      byte[] bytes = new byte[length];
      for(int index = 0; index < length; index++)
      {
         bytes[index] = (byte)(index % 251);
      }
      return bytes;
   }
}
