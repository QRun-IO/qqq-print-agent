/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The helper requests one print job causes, each run through the helper's own loop over the real Windows rules and a
/// Windows that exists only in memory. The station has three queues: one whose driver never answers, the Zebra queue
/// the job is for, and a laser queue. The service sends a helper three requests for a job (the list before the claim,
/// the lookup of the job's queue after the download, and the print), and each helper is a process of its own, so each
/// request here gets a station of its own. Together they show that a job reaches Windows for its own queue only. The
/// fourth request, the full discovery, is the one a job no longer sends: on this station it fails. No second process is
/// started, no printer is touched and the staged file is synthetic.
/// </summary>
public sealed class PrinterWorkerOneQueueTests
{
   /// <summary>The name of the queue the job is for.</summary>
   private const string Zebra = "Synthetic Zebra queue";



   /// <summary>The identifier of that queue: the first 24 hexadecimal digits of the SHA-256 of its name.</summary>
   private const string ZebraId = "6119358ca3e4aafc30d6a3da";



   /// <summary>The name of the queue whose driver never answers.</summary>
   private const string Broken = "Synthetic broken queue";



   /// <summary>The name of the laser queue, which nothing is wrong with and which the job is not for.</summary>
   private const string Third = "Synthetic laser queue";



   /// <summary>A synthetic ZPL label.</summary>
   private static readonly byte[] Label = Encoding.ASCII.GetBytes("^XA^FO50,50^FDsynthetic^FS^XZ");



   /// <summary>The settings read of the Zebra queue when it is the first queue a helper opens.</summary>
   private static readonly string[] ZebraRead =
   [
      $"OpenPrinter({Zebra})", "GetPrinterSize(0x201, 8)", "GetPrinter(0x201, 8, 232)", "ClosePrinter(0x201)",
      $"CreateDC(WINSPOOL, {Zebra}, 224 bytes)", "GetDeviceCaps(0xd01, 88)", "GetDeviceCaps(0xd01, 90)", "DeleteDC(0xd01)",
   ];



   /// <summary>
   /// The three requests of one job, in the order the service sends them. The list names all three queues from the two
   /// enumeration calls alone. The lookup answers with the Zebra queue and its media after reading that queue only. The
   /// print reads the Zebra queue's settings and spools the document on it. In all three, no call names the queue whose
   /// driver never answers or the laser queue.
   /// </summary>
   [Fact]
   public async Task RequestsOfOneJobAskOnlyTheJobsOwnQueue()
   {
      using StagedDocument staged = new(Label);

      (int listExit, PrinterWorkerHost.Response list, FakeWindowsPrinting listed) = await RunAsync(new PrinterWorkerHost.Request("list-without-media"));
      (int findExit, PrinterWorkerHost.Response find, FakeWindowsPrinting found) = await RunAsync(new PrinterWorkerHost.Request("find-with-media", PrinterId: ZebraId));
      (int printExit, PrinterWorkerHost.Response print, FakeWindowsPrinting printed) = await RunAsync(new PrinterWorkerHost.Request("print-file", staged.Job(ZebraId, "ZPL"), ContentPath: staged.Path));

      Assert.Equal((0, 0, 0), (listExit, findExit, printExit));
      Assert.Equal([Broken, Zebra, Third], list.Printers!.Select(printer => printer.Name), StringComparer.Ordinal);
      Assert.All(list.Printers!, printer => Assert.Null(printer.MediaProfiles));
      Assert.Equal(["EnumPrintersSize(6)", "EnumPrinters(6, 408)"], listed.Calls, StringComparer.Ordinal);

      PrinterInfo queue = Assert.Single(find.Printers!);
      Assert.Equal((ZebraId, Zebra), (queue.PrinterId, queue.Name));
      Assert.Equal(["PDF", "ZPL"], queue.Formats, StringComparer.Ordinal);
      Assert.Equal(WindowsPrinters.BuildMediaProfile(ZebraId, "ZDesigner ZT411-300dpi ZPL", SyntheticDevMode.Label(), 101600, 152400, 300, 300), Assert.Single(queue.MediaProfiles!));
      Assert.Equal(["EnumPrintersSize(6)", "EnumPrinters(6, 408)", .. ZebraRead], found.Calls, StringComparer.Ordinal);

      Assert.Equal((true, "4101"), (print.Success, print.SpoolerJobId));
      Assert.Equal(
      [
         "EnumPrintersSize(6)", "EnumPrinters(6, 408)", .. ZebraRead,
         $"OpenPrinter({Zebra})", "StartDocPrinter(0x202, QQQ job-1, RAW)", $"WritePrinter(0x202, {Label.Length} bytes, {Label.Length})", "EndDocPrinter(0x202)", "ClosePrinter(0x202)",
      ], printed.Calls, StringComparer.Ordinal);
      Assert.Equal(Label, Assert.Single(printed.RawDocuments).Bytes.ToArray());

      Assert.DoesNotContain([.. listed.Calls, .. found.Calls, .. printed.Calls], call => call.Contains(Broken, StringComparison.Ordinal) || call.Contains(Third, StringComparison.Ordinal));
   }



   /// <summary>
   /// The full discovery request on the same station fails at the queue whose driver never answers, with that failure as
   /// its answer. A job sent this request three times before; a job that still sent it could not print here.
   /// </summary>
   [Fact]
   public async Task FullDiscoveryRequestFailsOnTheSameStation()
   {
      (int exit, PrinterWorkerHost.Response response, FakeWindowsPrinting windows) = await RunAsync(new PrinterWorkerHost.Request("discover"));
      Assert.Equal(1, exit);
      Assert.Equal((false, "Synthetic: this queue's driver never answers."), (response.Success, response.Error));
      Assert.Null(response.Printers);
      Assert.Equal(["EnumPrintersSize(6)", "EnumPrinters(6, 408)", $"OpenPrinter({Broken})"], windows.Calls, StringComparer.Ordinal);
   }



   /// <summary>
   /// Runs the helper loop once, parented to process 4242, on one request and a station of its own: the queue whose driver
   /// never answers, the Zebra queue and a laser queue, in the order the spooler lists them, all three with machine
   /// defaults. Returns the helper's exit code, its single answer and the fake Windows that recorded what it was asked.
   /// </summary>
   private static async Task<(int Exit, PrinterWorkerHost.Response Response, FakeWindowsPrinting Windows)> RunAsync(PrinterWorkerHost.Request request)
   {
      FakeWindowsPrinting windows = new();
      _ = windows.Add(new FakeQueue(Broken, "Synthetic stuck driver", "192.0.2.20") { MachineDefaults = SyntheticDevMode.Label(), NeverAnswers = () => new TimeoutException("Synthetic: this queue's driver never answers.") });
      _ = windows.Add(new FakeQueue(Zebra, "ZDesigner ZT411-300dpi ZPL", "USB001") { MachineDefaults = SyntheticDevMode.Label() }.WithContext(1200, 1800));
      _ = windows.Add(new FakeQueue(Third, "Synthetic laser driver", "192.0.2.10") { MachineDefaults = SyntheticDevMode.Label() }.WithContext(1200, 1800));
      WindowsPrinters printers = new(windows, windows, windows, new FakePdfRenderer(windows.Calls));
      using StringReader input = new(JsonSerializer.Serialize(request, PrintApiClient.JsonOptions) + "\n");
      using StringWriter output = new();
      int exit = await PrinterWorkerHost.RunAsync(printers, new FakeSpoolerJobs(), new FakeParentWatch(), "4242", input, output);
      string line = Assert.Single(output.ToString().Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries));
      return (exit, Assert.IsType<PrinterWorkerHost.Response>(JsonSerializer.Deserialize<PrinterWorkerHost.Response>(line, PrintApiClient.JsonOptions)), windows);
   }
}
