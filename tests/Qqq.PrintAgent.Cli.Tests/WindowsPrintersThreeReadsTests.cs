/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The three ways the Windows adapter reads its queues, run against a Windows that exists only in memory: the full
/// discovery, which reads the settings of every queue; the list without media, which reads the settings of none; and the
/// lookup of one queue, which reads the settings of that queue only. The tests show that the three agree about every
/// kind of queue, and, by the calls the fake recorded, which queues each one asks. A queue whose driver never answers
/// stands in several of them for the printer that used to stop every job on the station. No print queue on this computer
/// is listed or opened.
/// </summary>
public sealed class WindowsPrintersThreeReadsTests
{
   /// <summary>The name of the label queue the tests ask for.</summary>
   private const string Target = "Synthetic Zebra queue";



   /// <summary>The identifier of that queue: the first 24 hexadecimal digits of the SHA-256 of its name.</summary>
   private const string TargetId = "6119358ca3e4aafc30d6a3da";



   /// <summary>The name of the queue whose driver never answers.</summary>
   private const string Broken = "Synthetic broken queue";



   /// <summary>The name of the laser queue, which nothing is wrong with and which the tests never ask for.</summary>
   private const string Third = "Synthetic laser queue";



   /// <summary>The two calls every read begins with on the station of three queues.</summary>
   private static readonly string[] Enumeration = ["EnumPrintersSize(6)", "EnumPrinters(6, 408)"];



   /// <summary>The settings read of the target queue when it is the first queue a read opens: its level 8 details, then its resolution from a device context built on them.</summary>
   private static readonly string[] TargetRead =
   [
      $"OpenPrinter({Target})", "GetPrinterSize(0x201, 8)", "GetPrinter(0x201, 8, 232)", "ClosePrinter(0x201)",
      $"CreateDC(WINSPOOL, {Target}, 224 bytes)", "GetDeviceCaps(0xd01, 88)", "GetDeviceCaps(0xd01, 90)", "DeleteDC(0xd01)",
   ];



   /// <summary>
   /// For every kind of queue (a label queue with media, a busy laser queue without readable defaults, a paused queue with
   /// media, two queues that need a person, and one the spooler listed without a name) the list without media reports
   /// what the full discovery reports, in the same order, minus the media profiles, which it leaves null; and the lookup
   /// of each queue by its identifier returns the full discovery's entry for it, media profiles included. An identifier
   /// no queue has is null.
   /// </summary>
   [Fact]
   public void ListAndLookupReportWhatTheFullDiscoveryReportsForEveryKindOfQueue()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Setup();
      byte[] defaults = SyntheticDevMode.Label();
      windows.Add(new FakeQueue(Target, "ZDesigner ZT411-300dpi ZPL", "USB001") { MachineDefaults = defaults }.WithContext(1200, 1800));
      windows.Add(new FakeQueue(Third, "Synthetic laser driver", "192.0.2.10") { Status = 0x400 });
      windows.Add(new FakeQueue("Synthetic paused queue", "Synthetic label driver", "USB002") { Status = 0x1, MachineDefaults = defaults }.WithContext(1200, 1800));
      windows.Add(new FakeQueue("Document writer (synthetic)", "Microsoft Print To PDF", "PORTPROMPT:") { MachineDefaults = defaults });
      windows.Add(new FakeQueue("Print to file (synthetic)", "Generic / Text Only", "FILE:") { Status = 0x80, MachineDefaults = defaults });
      windows.Add(new FakeQueue(null, null, null));

      IReadOnlyList<PrinterInfo> discovered = printers.Discover();
      IReadOnlyList<PrinterInfo> listed = printers.ListWithoutMedia();

      Assert.Equal(["PDF,ZPL", "PDF", "PDF", "", "", "PDF"], discovered.Select(printer => string.Join(',', printer.Formats)), StringComparer.Ordinal);
      Assert.Equal(["READY", "READY", "PAUSED", "UNKNOWN", "UNKNOWN", "READY"], discovered.Select(printer => printer.Status), StringComparer.Ordinal);
      Assert.Equal([1, 0, 1, 0, 0, 0], discovered.Select(printer => printer.MediaProfiles!.Count));
      Assert.Equal(discovered.Count, listed.Count);
      for(int index = 0; index < discovered.Count; index++)
      {
         AssertSameQueue(discovered[index], listed[index]);
         Assert.Null(listed[index].MediaProfiles);
         PrinterInfo found = Assert.IsType<PrinterInfo>(printers.FindWithMedia(discovered[index].PrinterId));
         AssertSameQueue(discovered[index], found);
         Assert.Equal(discovered[index].MediaProfiles, found.MediaProfiles);
      }
      Assert.Null(printers.FindWithMedia("000000000000000000000000"));
      Assert.Equal(0, windows.OpenPrinters);
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>The list without media is the two enumeration calls and nothing else: no queue is opened and no driver is asked, so the queue whose driver never answers is listed like the others.</summary>
   [Fact]
   public void ListWithoutMediaAsksNoQueueAnything()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Station();

      IReadOnlyList<PrinterInfo> listed = printers.ListWithoutMedia();

      Assert.Equal([Broken, Target, Third], listed.Select(printer => printer.Name), StringComparer.Ordinal);
      Assert.Equal(Enumeration, windows.Calls, StringComparer.Ordinal);
   }



   /// <summary>
   /// The lookup of one queue is the two enumeration calls and then the settings read of that queue. The queue whose
   /// driver never answers is listed before it and the laser queue after it, and neither is opened or asked anything.
   /// </summary>
   [Fact]
   public void LookupReadsTheSettingsOfTheQueueAskedForAndOfNoOther()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Station();

      PrinterInfo found = Assert.IsType<PrinterInfo>(printers.FindWithMedia(TargetId));

      Assert.Equal((TargetId, Target, "READY"), (found.PrinterId, found.Name, found.Status));
      Assert.Equal(["PDF", "ZPL"], found.Formats, StringComparer.Ordinal);
      Assert.Equal(WindowsPrinters.BuildMediaProfile(TargetId, "ZDesigner ZT411-300dpi ZPL", SyntheticDevMode.Label(), 101600, 152400, 300, 300), Assert.Single(found.MediaProfiles!));
      Assert.Equal([.. Enumeration, .. TargetRead], windows.Calls, StringComparer.Ordinal);
      Assert.Equal(0, windows.OpenPrinters);
      Assert.Equal(0, windows.OpenContexts);
   }



   /// <summary>
   /// The full discovery asks every queue, so the one whose driver never answers holds it: it fails at that queue, and
   /// the queues listed after it are never reached. This is the read a print job no longer makes.
   /// </summary>
   [Fact]
   public void FullDiscoveryIsHeldByTheQueueThatNeverAnswers()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Station();
      Assert.Throws<TimeoutException>(() => printers.Discover());
      Assert.Equal([.. Enumeration, $"OpenPrinter({Broken})"], windows.Calls, StringComparer.Ordinal);
   }



   /// <summary>A queue whose driver never answers holds its own lookup, as it must: its settings are what was asked for. No other queue is asked.</summary>
   [Fact]
   public void LookupOfTheQueueThatNeverAnswersIsHeldByItAlone()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Station();
      Assert.Throws<TimeoutException>(() => printers.FindWithMedia(WindowsPrinters.QueueIdentifier(Broken)));
      Assert.Equal([.. Enumeration, $"OpenPrinter({Broken})"], windows.Calls, StringComparer.Ordinal);
   }



   /// <summary>A queue that needs a person is found by its identifier as UNKNOWN with no formats and no media, and it is never opened: the lookup is the two enumeration calls.</summary>
   [Fact]
   public void LookupOfAQueueThatNeedsAPersonOpensNothing()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Station();
      windows.Add(new FakeQueue("Document writer (synthetic)", "Microsoft Print To PDF", "PORTPROMPT:") { MachineDefaults = SyntheticDevMode.Label() });

      PrinterInfo found = Assert.IsType<PrinterInfo>(printers.FindWithMedia("6c32da906f525a7720e3104d"));

      Assert.Equal("UNKNOWN", found.Status);
      Assert.Empty(found.Formats);
      Assert.Empty(found.MediaProfiles!);
      Assert.Equal(["EnumPrintersSize(6)", "EnumPrinters(6, 544)"], windows.Calls, StringComparer.Ordinal);
   }



   /// <summary>An identifier no queue has is absent after the two enumeration calls; no queue is opened to find that out.</summary>
   [Fact]
   public void LookupOfAnIdentifierNoQueueHasOpensNothing()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Station();
      Assert.Null(printers.FindWithMedia("000000000000000000000000"));
      Assert.Null(printers.FindWithMedia(Target));
      Assert.Equal([.. Enumeration, .. Enumeration], windows.Calls, StringComparer.Ordinal);
   }



   /// <summary>On a station with nothing installed the list is empty and the lookup finds nothing, each from the one sizing call.</summary>
   [Fact]
   public void NothingInstalled_IsAnEmptyListAndNoQueueFromOneCallEach()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Setup();
      Assert.Empty(printers.ListWithoutMedia());
      Assert.Null(printers.FindWithMedia(TargetId));
      Assert.Equal(["EnumPrintersSize(6)", "EnumPrintersSize(6)"], windows.Calls, StringComparer.Ordinal);
   }



   /// <summary>Two queues the spooler lists under one name have one identifier. The lookup refuses to choose between them, and refuses before it reads either.</summary>
   [Fact]
   public void TwoQueuesWithOneIdentifierAreRefusedBeforeEitherIsRead()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Setup();
      windows.Add(new FakeQueue(Target, "ZDesigner ZT411-300dpi ZPL", "USB001") { MachineDefaults = SyntheticDevMode.Label() }.WithContext(1200, 1800));
      windows.Add(new FakeQueue(Target, "ZDesigner ZT411-300dpi ZPL", "USB002") { MachineDefaults = SyntheticDevMode.Label() }.WithContext(1200, 1800));
      Assert.Throws<InvalidOperationException>(() => printers.FindWithMedia(TargetId));
      Assert.Equal(["EnumPrintersSize(6)", "EnumPrinters(6, 272)"], windows.Calls, StringComparer.Ordinal);
   }



   /// <summary>
   /// A spooler that cannot list its queues fails the list without media and the lookup exactly as it fails the full
   /// discovery: the sizing call's Windows error is thrown and the list is never read, and a list read that fails is
   /// thrown with its Windows error. Neither is taken for a station without printers or a printer that is gone.
   /// </summary>
   [Theory]
   [InlineData("list", "EnumPrintersSize", 1)]
   [InlineData("list", "EnumPrinters", 2)]
   [InlineData("lookup", "EnumPrintersSize", 1)]
   [InlineData("lookup", "EnumPrinters", 2)]
   public void SpoolerThatCannotListItsQueuesFailsTheListAndTheLookup(string read, string call, int callsMade)
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Station();
      windows.Fail(call, 1722);
      Action reading = string.Equals(read, "list", StringComparison.Ordinal) ? () => _ = printers.ListWithoutMedia() : () => _ = printers.FindWithMedia(TargetId);
      Win32Exception failure = Assert.Throws<Win32Exception>(reading);
      Assert.Equal(1722, failure.NativeErrorCode);
      Assert.Equal(Enumeration.Take(callsMade), windows.Calls, StringComparer.Ordinal);
   }



   /// <summary>A queue identifier is the first 24 hexadecimal digits, in lower case, of the SHA-256 of the queue name; the empty name has one too.</summary>
   [Theory]
   [InlineData(Target, TargetId)]
   [InlineData(Third, "f00632792c722747d33552e2")]
   [InlineData("", "e3b0c44298fc1c149afbf4c8")]
   public void QueueIdentifierIsTheStartOfTheHashOfTheName(string name, string identifier)
   {
      Assert.Equal(identifier, WindowsPrinters.QueueIdentifier(name));
      Assert.True(WindowsPrinters.IsQueueIdentifier(WindowsPrinters.QueueIdentifier(name)));
   }



   /// <summary>Only text of exactly 24 hexadecimal digits in lower case has the form of a queue identifier: not a queue name, not upper case, not one digit short or long, not a digit outside 0 to 9 and a to f, and not nothing.</summary>
   [Theory]
   [InlineData("6119358ca3e4aafc30d6a3da", true)]
   [InlineData("000000000000000000000000", true)]
   [InlineData("ffffffffffffffffffffffff", true)]
   [InlineData("6119358CA3E4AAFC30D6A3DA", false)]
   [InlineData("6119358ca3e4aafc30d6a3d", false)]
   [InlineData("6119358ca3e4aafc30d6a3da0", false)]
   [InlineData("6119358ca3e4aafc30d6a3dg", false)]
   [InlineData("6119358ca3e4aafc30d6a3d/", false)]
   [InlineData("6119358ca3e4aafc30d6a3d:", false)]
   [InlineData("6119358ca3e4aafc30d6a3d`", false)]
   [InlineData(" 119358ca3e4aafc30d6a3da", false)]
   [InlineData("Synthetic Zebra queue", false)]
   [InlineData("", false)]
   [InlineData(null, false)]
   public void OnlyTwentyFourLowerCaseHexadecimalDigitsHaveTheFormOfAQueueIdentifier(string? text, bool expected)
      => Assert.Equal(expected, WindowsPrinters.IsQueueIdentifier(text));



   /// <summary>Requires two reports of one queue to agree on everything that identifies and classifies it; media profiles are compared by the caller, and no read sets the operator's names.</summary>
   private static void AssertSameQueue(PrinterInfo expected, PrinterInfo actual)
   {
      Assert.Equal((expected.PrinterId, expected.Name, expected.DriverName, expected.PortName, expected.Status), (actual.PrinterId, actual.Name, actual.DriverName, actual.PortName, actual.Status));
      Assert.Equal(expected.Formats, actual.Formats, StringComparer.Ordinal);
      Assert.Equal((null, null, null), (actual.DisplayName, actual.Description, actual.Location));
      Assert.Equal(WindowsPrinters.QueueIdentifier(actual.Name), actual.PrinterId);
   }



   /// <summary>Builds the rules over a fake Windows with no queues yet.</summary>
   private static (WindowsPrinters Printers, FakeWindowsPrinting Windows) Setup()
   {
      FakeWindowsPrinting windows = new();
      return (new WindowsPrinters(windows, windows, windows, new FakePdfRenderer(windows.Calls)), windows);
   }



   /// <summary>
   /// Builds the rules over a station of three queues, in the order the spooler lists them: one whose driver never
   /// answers, the label queue the tests ask for, and a laser queue. All three have machine defaults, so all three would
   /// be read by a read that asked every queue.
   /// </summary>
   private static (WindowsPrinters Printers, FakeWindowsPrinting Windows) Station()
   {
      (WindowsPrinters printers, FakeWindowsPrinting windows) = Setup();
      windows.Add(new FakeQueue(Broken, "Synthetic stuck driver", "192.0.2.20") { MachineDefaults = SyntheticDevMode.Label(), NeverAnswers = () => new TimeoutException("Synthetic: this queue's driver never answers.") });
      windows.Add(new FakeQueue(Target, "ZDesigner ZT411-300dpi ZPL", "USB001") { MachineDefaults = SyntheticDevMode.Label() }.WithContext(1200, 1800));
      windows.Add(new FakeQueue(Third, "Synthetic laser driver", "192.0.2.10") { MachineDefaults = SyntheticDevMode.Label() }.WithContext(1200, 1800));
      return (printers, windows);
   }
}
