/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// How a queue's machine default settings are read and turned into a media profile, and how a job's own settings are
/// prepared through the driver, run against a Windows that exists only in memory. Each test asserts the exact calls sent
/// and their order, with the settings bytes that were handed over, which is the evidence that the reads reach Windows the
/// way they did before the calls were moved behind <see cref="ISpooler"/>, <see cref="IPrinterSettings"/> and
/// <see cref="IGdiPrinter"/>. No print queue on this computer is opened.
/// </summary>
public sealed class PrinterSettingsTests
{
   private const string Queue = "Synthetic label queue";
   private const string Driver = "Synthetic label driver";
   private const string Port = "USB001";
   private const string PrinterId = "0123456789abcdef01234567";

   /// <summary>The four calls that read the machine defaults of the test queue through the first handle: a pointer plus 224 bytes of settings.</summary>
   private static readonly string[] DefaultsRead = [$"OpenPrinter({Queue})", "GetPrinterSize(0x201, 8)", "GetPrinter(0x201, 8, 232)", "ClosePrinter(0x201)"];

   /// <summary>The four calls that ask a device context built from the settings for its resolution.</summary>
   private static readonly string[] ResolutionRead = [$"CreateDC(WINSPOOL, {Queue}, 224 bytes)", "GetDeviceCaps(0xd01, 88)", "GetDeviceCaps(0xd01, 90)", "DeleteDC(0xd01)"];



   /// <summary>
   /// The defaults are read at level 8 (the machine-wide ones, not a user's), copied whole with their private part, and the
   /// resolution is asked of a device context created from exactly those bytes. The queue is closed before the context is
   /// made and the context is deleted.
   /// </summary>
   [Fact]
   public void MachineDefaultsAreReadAtLevelEightAndResolutionComesFromTheirOwnContext()
   {
      byte[] defaults = SyntheticDevMode.Label();
      (WindowsPrinters printers, FakeWindowsPrinting windows, _) = Setup(defaults);
      WindowsPrinters.QueueDefaultMedia media = Assert.IsType<WindowsPrinters.QueueDefaultMedia>(printers.ReadQueueMedia(PrinterId, Queue, Driver, Port));
      Assert.Equal(defaults, media.DevMode);
      Assert.Equal(WindowsPrinters.BuildMediaProfile(PrinterId, Driver, defaults, 101600, 152400, 300, 300), media.Profile);
      Assert.Equal([.. DefaultsRead, .. ResolutionRead], windows.Calls, StringComparer.Ordinal);
      Assert.Equal(defaults, Assert.Single(windows.ContextModes));
      Assert.Equal(0, windows.OpenPrinters);
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>A positive explicit resolution in the settings is used as it is and no device context is created; a symbolic quality value (negative) is not a resolution, so the context is asked.</summary>
   [Fact]
   public void ExplicitResolutionNeedsNoContextButASymbolicQualityDoes()
   {
      byte[] explicitDpi = SyntheticDevMode.Label().WithFields(SyntheticDevMode.Orientation | SyntheticDevMode.PaperDimensions | SyntheticDevMode.Resolution).WithShort(90, 203).WithShort(96, 204);
      (WindowsPrinters printers, FakeWindowsPrinting windows, _) = Setup(explicitDpi);
      PrinterMediaProfile profile = Assert.IsType<PrinterMediaProfile>(printers.ReadQueueMedia(PrinterId, Queue, Driver, Port)?.Profile);
      Assert.Equal((203, 204), (profile.DpiX, profile.DpiY));
      Assert.Equal(DefaultsRead, windows.Calls, StringComparer.Ordinal);

      byte[] symbolic = explicitDpi.ToArray().WithShort(90, -4);
      (printers, windows, _) = Setup(symbolic);
      profile = Assert.IsType<PrinterMediaProfile>(printers.ReadQueueMedia(PrinterId, Queue, Driver, Port)?.Profile);
      Assert.Equal((300, 300), (profile.DpiX, profile.DpiY));
      Assert.Equal([.. DefaultsRead, .. ResolutionRead], windows.Calls, StringComparer.Ordinal);
   }



   /// <summary>
   /// Settings that name a paper identifier instead of dimensions are resolved through the driver's two paper lists, asked
   /// with the captured settings: both counts first, then both lists into buffers sized from the count. The one entry with
   /// that identifier gives the size.
   /// </summary>
   [Fact]
   public void PaperIdentifierIsResolvedThroughTheDriversPaperLists()
   {
      byte[] defaults = PaperIdSettings(257);
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakeQueue queue) = Setup(defaults);
      queue.PaperIds = [1, 9, 257];
      queue.PaperSizes = [(2159, 2794), (2100, 2970), (1016, 1524)];
      PrinterMediaProfile profile = Assert.IsType<PrinterMediaProfile>(printers.ReadQueueMedia(PrinterId, Queue, Driver, Port)?.Profile);
      Assert.Equal((101600, 152400), (profile.WidthMicrons, profile.HeightMicrons));
      Assert.Equal(
      [
         .. DefaultsRead,
         $"DeviceCapabilities({Queue}, {Port}, 2, count)",
         $"DeviceCapabilities({Queue}, {Port}, 3, count)",
         $"DeviceCapabilities({Queue}, {Port}, 2, 6 bytes)",
         $"DeviceCapabilities({Queue}, {Port}, 3, 24 bytes)",
         .. ResolutionRead,
      ], windows.Calls, StringComparer.Ordinal);
      Assert.All(windows.CapabilityModes, mode => Assert.Equal(defaults, mode));
   }



   /// <summary>
   /// A driver whose paper lists cannot be trusted gives no size and so no profile, without guessing a standard form: no
   /// entries, too many, lists of different length, a read that returns another count, no entry or two entries with the
   /// identifier, or a size outside 0.1 mm to 2 m. The queue itself stays readable.
   /// </summary>
   [Theory]
   [InlineData("no entries", 1)]
   [InlineData("too many entries", 1)]
   [InlineData("lists differ", 2)]
   [InlineData("first read differs", 3)]
   [InlineData("second read differs", 4)]
   [InlineData("no match", 4)]
   [InlineData("two matches", 4)]
   [InlineData("zero width", 4)]
   [InlineData("width over two meters", 4)]
   [InlineData("zero height", 4)]
   [InlineData("height over two meters", 4)]
   public void UntrustworthyPaperListGivesNoProfile(string fault, int capabilityCalls)
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakeQueue queue) = Setup(PaperIdSettings(257));
      queue.PaperIds = [1, 257];
      queue.PaperSizes = [(2159, 2794), (1016, 1524)];
      switch(fault)
      {
         case "no entries":
            queue.CountAnswers[2] = 0;
            break;
         case "too many entries":
            queue.CountAnswers[2] = 65536;
            break;
         case "lists differ":
            queue.CountAnswers[3] = 3;
            break;
         case "first read differs":
            queue.ReadAnswers[2] = 1;
            break;
         case "second read differs":
            queue.ReadAnswers[3] = -1;
            break;
         case "no match":
            queue.PaperIds = [1, 9];
            break;
         case "two matches":
            queue.PaperIds = [257, 257];
            break;
         case "zero width":
            queue.PaperSizes = [(2159, 2794), (0, 1524)];
            break;
         case "width over two meters":
            queue.PaperSizes = [(2159, 2794), (20_001, 1524)];
            break;
         case "zero height":
            queue.PaperSizes = [(2159, 2794), (1016, 0)];
            break;
         default:
            queue.PaperSizes = [(2159, 2794), (1016, 20_001)];
            break;
      }
      WindowsPrinters.QueueDefaultMedia media = Assert.IsType<WindowsPrinters.QueueDefaultMedia>(printers.ReadQueueMedia(PrinterId, Queue, Driver, Port));
      Assert.Null(media.Profile);
      Assert.Equal(capabilityCalls, windows.Calls.Count(call => call.StartsWith("DeviceCapabilities(", StringComparison.Ordinal)));
      Assert.Equal(0, windows.OpenPrinters);
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>The largest paper list the rules accept (65,535 entries) is still read, and settings that name neither dimensions nor an identifier are never looked up in the driver.</summary>
   [Fact]
   public void PaperListBoundAndSettingsWithoutAnyPaperField()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakeQueue queue) = Setup(PaperIdSettings(257));
      queue.PaperIds = [257];
      queue.PaperSizes = [(1016, 1524)];
      queue.CountAnswers[2] = queue.CountAnswers[3] = queue.ReadAnswers[2] = queue.ReadAnswers[3] = 65535;
      Assert.NotNull(printers.ReadQueueMedia(PrinterId, Queue, Driver, Port)?.Profile);
      Assert.Equal($"DeviceCapabilities({Queue}, {Port}, 3, 524280 bytes)", windows.Calls[7]);

      (printers, windows, _) = Setup(SyntheticDevMode.Label().WithFields(SyntheticDevMode.Orientation));
      WindowsPrinters.QueueDefaultMedia media = Assert.IsType<WindowsPrinters.QueueDefaultMedia>(printers.ReadQueueMedia(PrinterId, Queue, Driver, Port));
      Assert.Null(media.Profile);
      Assert.Equal([.. DefaultsRead, .. ResolutionRead], windows.Calls, StringComparer.Ordinal);
   }



   /// <summary>A queue that cannot be opened has no media profile and stays discoverable: the Windows error is swallowed here, nothing else is asked and there is no handle to close.</summary>
   [Fact]
   public void QueueThatCannotBeOpenedHasNoMediaProfile()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakeQueue queue) = Setup();
      queue.OpenError = 5;
      Assert.Null(printers.ReadQueueMedia(PrinterId, Queue, Driver, Port));
      Assert.Equal([$"OpenPrinter({Queue})"], windows.Calls, StringComparer.Ordinal);
      Win32Exception failure = Assert.Throws<Win32Exception>(() => printers.ReadMachineDevMode(Queue));
      Assert.Equal(5, failure.NativeErrorCode);
   }



   /// <summary>
   /// The answer's size is bounded before any buffer is asked for: smaller than a pointer plus the fixed part of the settings
   /// (110 bytes) or larger than 1 MiB means no defaults, and the queue is closed without a read. Exactly at either bound the
   /// read is made.
   /// </summary>
   [Theory]
   [InlineData(0u, false)]
   [InlineData(109u, false)]
   [InlineData(110u, true)]
   [InlineData(1_048_576u, true)]
   [InlineData(1_048_577u, false)]
   public void DefaultsSizeIsBoundedBeforeTheRead(uint reported, bool read)
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakeQueue queue) = Setup(SyntheticDevMode.Label().WithShort(68, 102).WithShort(70, 0));
      queue.ReportedDefaultsSize = reported;
      byte[]? mode = printers.ReadMachineDevMode(Queue);
      Assert.Equal(read, mode is not null);
      Assert.Equal(read ? 102 : null, mode?.Length);
      Assert.Equal(read
         ? [$"OpenPrinter({Queue})", "GetPrinterSize(0x201, 8)", $"GetPrinter(0x201, 8, {reported})", "ClosePrinter(0x201)"]
         : new[] { $"OpenPrinter({Queue})", "GetPrinterSize(0x201, 8)", "ClosePrinter(0x201)" }, windows.Calls, StringComparer.Ordinal);
      Assert.Equal(0, windows.OpenPrinters);
   }



   /// <summary>A read that Windows refuses keeps its error for a caller that asked for the settings, is "no profile" for discovery, and closes the queue either way.</summary>
   [Fact]
   public void RefusedReadKeepsItsWindowsErrorAndClosesTheQueue()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakeQueue queue) = Setup();
      queue.GetPrinterError = 1722;
      Win32Exception failure = Assert.Throws<Win32Exception>(() => printers.ReadMachineDevMode(Queue));
      Assert.Equal(1722, failure.NativeErrorCode);
      Assert.Equal(DefaultsRead, windows.Calls, StringComparer.Ordinal);
      Assert.Equal(0, windows.OpenPrinters);
      Assert.Null(printers.ReadQueueMedia(PrinterId, Queue, Driver, Port));
      Assert.Equal(0, windows.OpenPrinters);
   }



   /// <summary>
   /// The pointer and the two lengths inside the answer are checked against the answer's size before anything is copied: a
   /// null pointer, a pointer into the pointer field itself or past the last place a fixed part fits, a fixed part shorter
   /// than 102 bytes and settings that run past the end of the answer all mean no defaults.
   /// </summary>
   [Theory]
   [InlineData("null pointer")]
   [InlineData("pointer before the settings")]
   [InlineData("pointer past the last fixed part")]
   [InlineData("pointer before the answer")]
   [InlineData("fixed part too short")]
   [InlineData("settings run past the answer")]
   public void PointerAndLengthsInsideTheAnswerAreChecked(string fault)
   {
      byte[] defaults = SyntheticDevMode.Label();
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakeQueue queue) = Setup(defaults);
      switch(fault)
      {
         case "null pointer":
            queue.NullDevModePointer = true;
            break;
         case "pointer before the settings":
            queue.DevModeOffset = IntPtr.Size - 1;
            break;
         case "pointer past the last fixed part":
            queue.DevModeOffset = 232 - 101;
            break;
         case "pointer before the answer":
            queue.DevModeOffset = -16;
            break;
         case "fixed part too short":
            _ = defaults.WithShort(68, 101);
            break;
         default:
            _ = defaults.WithShort(70, 5);
            break;
      }
      Assert.Null(printers.ReadMachineDevMode(Queue));
      Assert.Equal(DefaultsRead, windows.Calls, StringComparer.Ordinal);
      Assert.Equal(0, windows.OpenPrinters);
   }



   /// <summary>Exactly the settings are copied (their fixed and private parts), from where the pointer says they are, even when the answer is larger than they need.</summary>
   [Fact]
   public void OnlyTheSettingsAreCopiedOutOfALargerAnswer()
   {
      byte[] defaults = SyntheticDevMode.Label();
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakeQueue queue) = Setup(defaults);
      queue.ReportedDefaultsSize = 4096;
      queue.DevModeOffset = 512;
      Assert.Equal(defaults, printers.ReadMachineDevMode(Queue));
      Assert.Equal("GetPrinter(0x201, 8, 4096)", windows.Calls[2]);
   }



   /// <summary>When Windows will not create a device context from the settings, the resolution is unknown and the queue has no profile; no context is read or deleted.</summary>
   [Fact]
   public void RefusedContextMeansUnknownResolutionAndNoProfile()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakeQueue queue) = Setup();
      queue.CreateContextError = 1801;
      WindowsPrinters.QueueDefaultMedia media = Assert.IsType<WindowsPrinters.QueueDefaultMedia>(printers.ReadQueueMedia(PrinterId, Queue, Driver, Port));
      Assert.Null(media.Profile);
      Assert.Equal([.. DefaultsRead, ResolutionRead[0]], windows.Calls, StringComparer.Ordinal);
   }



   /// <summary>A job that selected no media profile is left on its existing path: nothing is read and nothing is required.</summary>
   [Fact]
   public void JobWithoutASelectionReadsNothing()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, _) = Setup();
      Assert.Null(printers.RequireCurrentMedia(Printer(), Job(new PrintOptions())));
      Assert.Empty(windows.Calls);
   }



   /// <summary>A job that selected the queue's current default gets the freshly read settings back; one whose selection no longer matches, or whose queue can no longer be read, is refused as a media mismatch before anything is submitted.</summary>
   [Fact]
   public void SelectedMediaIsReadAgainAndMustStillMatch()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakeQueue queue) = Setup();
      PrinterMediaProfile profile = Assert.IsType<PrinterMediaProfile>(printers.ReadQueueMedia(PrinterId, Queue, Driver, Port)?.Profile);
      PrintJob job = Job(new PrintOptions(MediaProfile: new PrinterMediaSelection(profile.Id, profile.Revision)));
      windows.Calls.Clear();
      WindowsPrinters.QueueDefaultMedia current = Assert.IsType<WindowsPrinters.QueueDefaultMedia>(printers.RequireCurrentMedia(Printer(), job));
      Assert.Equal(profile, current.Profile);
      Assert.Equal(queue.MachineDefaults, current.DevMode);
      Assert.Equal(
      [
         $"OpenPrinter({Queue})", "GetPrinterSize(0x202, 8)", "GetPrinter(0x202, 8, 232)", "ClosePrinter(0x202)",
         $"CreateDC(WINSPOOL, {Queue}, 224 bytes)", "GetDeviceCaps(0xd02, 88)", "GetDeviceCaps(0xd02, 90)", "DeleteDC(0xd02)",
      ], windows.Calls, StringComparer.Ordinal);

      queue.MachineDefaults![^1] ^= 0xff;
      Assert.Throws<PrinterMediaMismatchException>(() => printers.RequireCurrentMedia(Printer(), job));
      queue.OpenError = 5;
      Assert.Throws<PrinterMediaMismatchException>(() => printers.RequireCurrentMedia(Printer(), job));
      Assert.Equal(0, windows.OpenPrinters);
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>
   /// A selected PDF job's settings are a copy with one driver copy and 100 percent scale, merged by the driver with the
   /// input and output flags only (10: no prompt, no change to the queue), then resolved again through a context built
   /// from the merged bytes. The captured defaults are not changed.
   /// </summary>
   [Fact]
   public void SelectedPdfSettingsAreMergedByTheDriverAndResolvedAgain()
   {
      byte[] defaults = SyntheticDevMode.Label().WithFields(SyntheticDevMode.Orientation | SyntheticDevMode.PaperDimensions | SyntheticDevMode.Scale | SyntheticDevMode.Copies).WithShort(84, 50).WithShort(86, 3);
      byte[] original = defaults.ToArray();
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakeQueue queue) = Setup(defaults);
      queue.Merge = input =>
      {
         byte[] merged = input.ToArray();
         merged[^1] = 0x11;
         return merged;
      };
      WindowsPrinters.QueueDefaultMedia media = Assert.IsType<WindowsPrinters.QueueDefaultMedia>(printers.ReadQueueMedia(PrinterId, Queue, Driver, Port));
      windows.Calls.Clear();
      byte[] prepared = printers.PreparePdfMode(Printer(), media);
      Assert.Equal(
      [
         $"OpenPrinter({Queue})", $"DocumentPropertiesSize(0x202, {Queue})", $"DocumentProperties(0x202, {Queue}, 224 bytes, 224 bytes, 10)", "ClosePrinter(0x202)",
         $"CreateDC(WINSPOOL, {Queue}, 224 bytes)", "GetDeviceCaps(0xd02, 88)", "GetDeviceCaps(0xd02, 90)", "DeleteDC(0xd02)",
      ], windows.Calls, StringComparer.Ordinal);
      byte[] asked = Assert.Single(windows.MergeInputs);
      Assert.Equal((100, 1), (SyntheticDevMode.Short(asked, 84), SyntheticDevMode.Short(asked, 86)));
      Assert.Equal(0x11, prepared[^1]);
      Assert.Equal(prepared, windows.ContextModes[^1]);
      Assert.Equal(original, defaults);
      Assert.Equal(original, media.DevMode);
   }



   /// <summary>A driver that changes the paper while merging the job's settings is refused as a media mismatch, with the queue closed and the context deleted.</summary>
   [Fact]
   public void DriverThatChangesThePaperWhileMergingIsRefused()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakeQueue queue) = Setup();
      WindowsPrinters.QueueDefaultMedia media = Assert.IsType<WindowsPrinters.QueueDefaultMedia>(printers.ReadQueueMedia(PrinterId, Queue, Driver, Port));
      queue.Merge = input => input.ToArray().WithShort(80, 508);
      PrinterMediaMismatchException refusal = Assert.Throws<PrinterMediaMismatchException>(() => printers.PreparePdfMode(Printer(), media));
      Assert.Equal("The driver changed selected media while applying job-level copies and scale.", refusal.Message);
      Assert.Equal(0, windows.OpenPrinters);
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>
   /// Explicit job paper is applied to a fresh read of the machine defaults and merged by the driver: read the defaults,
   /// close, open again, ask the settings size, merge with flags 10, close. The settings handed to the driver carry the
   /// requested size as a user-defined form in tenths of a millimeter.
   /// </summary>
   [Fact]
   public void JobPaperIsAppliedToAFreshReadOfTheDefaultsAndMerged()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, _) = Setup();
      byte[] prepared = printers.PrepareCustomPdfMode(Printer(), (101600, 25400));
      Assert.Equal(
      [
         .. DefaultsRead,
         $"OpenPrinter({Queue})", $"DocumentPropertiesSize(0x202, {Queue})", $"DocumentProperties(0x202, {Queue}, 224 bytes, 224 bytes, 10)", "ClosePrinter(0x202)",
      ], windows.Calls, StringComparer.Ordinal);
      byte[] asked = Assert.Single(windows.MergeInputs);
      Assert.Equal(0xfu, SyntheticDevMode.Fields(asked));
      Assert.Equal((1, 256, 254, 1016), (SyntheticDevMode.Short(asked, 76), SyntheticDevMode.Short(asked, 78), SyntheticDevMode.Short(asked, 80), SyntheticDevMode.Short(asked, 82)));
      Assert.Equal(asked, prepared);
      Assert.Equal(0, windows.OpenPrinters);
   }



   /// <summary>Job paper needs machine defaults to start from: a queue without readable ones is refused as a media mismatch, and a queue that cannot be opened keeps its Windows error.</summary>
   [Fact]
   public void JobPaperNeedsReadableMachineDefaults()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakeQueue queue) = Setup();
      queue.MachineDefaults = null;
      PrinterMediaMismatchException refusal = Assert.Throws<PrinterMediaMismatchException>(() => printers.PrepareCustomPdfMode(Printer(), (101600, 25400)));
      Assert.Equal("The queue has no readable machine defaults to apply job paper to.", refusal.Message);
      Assert.Equal([$"OpenPrinter({Queue})", "GetPrinterSize(0x201, 8)", "ClosePrinter(0x201)"], windows.Calls, StringComparer.Ordinal);

      queue.OpenError = 5;
      Assert.Equal(5, Assert.Throws<Win32Exception>(() => printers.PrepareCustomPdfMode(Printer(), (101600, 25400))).NativeErrorCode);
   }



   /// <summary>
   /// The driver's merge is bounded and checked: a settings size under 102 bytes or over 1 MiB, a result other than 1, a
   /// fixed part shorter than 102 bytes or settings longer than the buffer are each refused as a media mismatch with the
   /// queue closed; a queue that cannot be opened for the merge keeps its Windows error.
   /// </summary>
   [Theory]
   [InlineData("size too small", "The driver did not provide a bounded document-settings buffer.", 2)]
   [InlineData("size too large", "The driver did not provide a bounded document-settings buffer.", 2)]
   [InlineData("size unknown", "The driver did not provide a bounded document-settings buffer.", 2)]
   [InlineData("refused", "The driver refused job-level document settings.", 3)]
   [InlineData("cancelled", "The driver refused job-level document settings.", 3)]
   [InlineData("fixed part too short", "The driver returned incomplete document settings.", 3)]
   [InlineData("settings longer than the buffer", "The driver returned incomplete document settings.", 3)]
   public void DriverMergeIsBoundedAndChecked(string fault, string message, int callsOnTheSecondHandle)
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakeQueue queue) = Setup();
      switch(fault)
      {
         case "size too small":
            queue.SettingsSize = 101;
            break;
         case "size too large":
            queue.SettingsSize = 1_048_577;
            break;
         case "size unknown":
            queue.SettingsSize = -1;
            break;
         case "refused":
            queue.MergeResult = 0;
            break;
         case "cancelled":
            queue.MergeResult = 2;
            break;
         case "fixed part too short":
            queue.Merge = input => input.ToArray().WithShort(68, 101);
            break;
         default:
            queue.Merge = input => input.ToArray().WithShort(70, 5);
            break;
      }
      PrinterMediaMismatchException refusal = Assert.Throws<PrinterMediaMismatchException>(() => printers.PrepareCustomPdfMode(Printer(), (101600, 50800)));
      Assert.Equal(message, refusal.Message);
      Assert.Equal(callsOnTheSecondHandle, windows.Calls.Count(call => call.Contains("(0x202", StringComparison.Ordinal)));
      Assert.Equal("ClosePrinter(0x202)", windows.Calls[^1]);
      Assert.Equal(0, windows.OpenPrinters);
   }



   /// <summary>A queue that can be read but not opened again for the merge keeps its Windows error, and a driver buffer larger than the settings it returns is cut to the settings.</summary>
   [Fact]
   public void MergeOpenFailureKeepsItsErrorAndALargerBufferIsCutToTheSettings()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakeQueue queue) = Setup();
      windows.Fail("OpenPrinter", 5, occurrence: 2);
      Assert.Equal(5, Assert.Throws<Win32Exception>(() => printers.PrepareCustomPdfMode(Printer(), (101600, 25400))).NativeErrorCode);
      Assert.Equal([.. DefaultsRead, $"OpenPrinter({Queue})"], windows.Calls, StringComparer.Ordinal);

      queue.SettingsSize = 1_048_576;
      byte[] prepared = printers.PrepareCustomPdfMode(Printer(), (101600, 25400));
      Assert.Equal(224, prepared.Length);
      Assert.Equal($"DocumentProperties(0x203, {Queue}, 1048576 bytes, 224 bytes, 10)", windows.Calls[^2]);
   }



   /// <summary>A queue without machine defaults is still a queue: discovery gets no media for it, and a selected PDF job with no complete settings to check is refused before a document is started.</summary>
   [Fact]
   public void QueueWithoutMachineDefaultsHasNoMediaAndCannotCarryASelectedPdf()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakeQueue queue) = Setup();
      queue.MachineDefaults = null;
      Assert.Null(printers.ReadQueueMedia(PrinterId, Queue, Driver, Port));
      Assert.Equal([$"OpenPrinter({Queue})", "GetPrinterSize(0x201, 8)", "ClosePrinter(0x201)"], windows.Calls, StringComparer.Ordinal);

      PrinterMediaProfile selected = new("default", new string('a', 64), "Selected", 101600, 152400, 300, 300);
      int started = 0;
      foreach(byte[]? settings in new[] { null, new byte[101] })
      {
         PrinterMediaMismatchException refusal = Assert.Throws<PrinterMediaMismatchException>(() => WindowsPrinters.StartValidatedPdfDocument(settings, selected, _ => 300, () => ++started));
         Assert.Equal("The selected PDF media has no complete machine defaults.", refusal.Message);
      }
      Assert.Equal(0, started);
   }



   /// <summary>A driver that drops its support for job-level copies or scale while job paper is merged is refused as a media mismatch.</summary>
   [Fact]
   public void DriverThatDropsCopySupportWhileMergingJobPaperIsRefused()
   {
      byte[] defaults = SyntheticDevMode.Label().WithFields(SyntheticDevMode.Orientation | SyntheticDevMode.PaperDimensions | SyntheticDevMode.Copies).WithShort(86, 1);
      (WindowsPrinters printers, FakeWindowsPrinting windows, FakeQueue queue) = Setup(defaults);
      queue.Merge = input => input.ToArray().WithFields(SyntheticDevMode.Fields(input) & ~SyntheticDevMode.Copies);
      PrinterMediaMismatchException refusal = Assert.Throws<PrinterMediaMismatchException>(() => printers.PrepareCustomPdfMode(Printer(), (101600, 25400)));
      Assert.Equal("The driver changed support for job-level copies or scale.", refusal.Message);
      Assert.Equal(0, windows.OpenPrinters);
   }



   /// <summary>The adapter over the real Windows can be built without asking Windows anything; only its members call out.</summary>
   [Fact]
   public void RealAdapterIsBuiltWithoutCallingWindows()
   {
      IPrinterBackend backend = new WindowsPrinters();
      Assert.IsType<WindowsPrinters>(backend);
   }



   /// <summary>Builds the rules over a fake Windows that has the one test queue, with a 4 by 6 inch, 300 dpi device context.</summary>
   private static (WindowsPrinters Printers, FakeWindowsPrinting Windows, FakeQueue Queue) Setup(byte[]? defaults = null)
   {
      FakeWindowsPrinting windows = new();
      FakeQueue queue = windows.Add(new FakeQueue(Queue, Driver, Port) { MachineDefaults = defaults ?? SyntheticDevMode.Label() }.WithContext(1200, 1800));
      return (new WindowsPrinters(windows, windows, windows, new FakePdfRenderer(windows.Calls)), windows, queue);
   }



   /// <summary>Settings that name a paper identifier and carry no explicit dimensions.</summary>
   private static byte[] PaperIdSettings(short paperId) => SyntheticDevMode.Label().WithFields(SyntheticDevMode.Orientation | SyntheticDevMode.PaperSize).WithShort(78, paperId);



   /// <summary>The test queue as discovery reports it.</summary>
   private static PrinterInfo Printer() => new(PrinterId, Queue, Driver, Port, "READY", ["PDF"]);



   /// <summary>A PDF job for the test queue with the given options.</summary>
   private static PrintJob Job(PrintOptions options) => new("job-1", PrinterId, "Synthetic", "PDF", new string('0', 64), 1, options, "2026-11-01T00:00:00Z", "QUEUED");
}
