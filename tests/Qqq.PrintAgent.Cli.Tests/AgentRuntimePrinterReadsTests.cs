/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Which of the three printer reads the running service uses for what, and what a printer that cannot be read stops. The
/// full discovery asks every queue's driver, so one queue whose driver hangs or fails makes it fail; these tests make it
/// fail and show what goes on working: jobs for other printers, operator and server cancellations, test prints and the
/// management pipe. They also show what does not: a connection that has not delivered an inventory yet claims nothing
/// until the printers can be read. Everything runs in a real agent runtime against the scripted server and a fake
/// printer; no physical printer is involved.
/// </summary>
public sealed class AgentRuntimePrinterReadsTests
{
   /// <summary>The reason the fake printer gives when it is told that its full discovery fails.</summary>
   private const string Unreadable = "Synthetic: the printer operation timed out.";



   /// <summary>
   /// The full discovery starts failing on a connection that has already delivered its inventory. The connection stays
   /// up, nothing is sent in place of the inventory, and jobs go on being claimed and printed: each is listed for
   /// before its claim and has its own printer looked up once. The failure is logged as an error once, however many
   /// rounds it lasts, and its end is logged once. The journal names the job's printer by the operator's name for it,
   /// which reaches the worker through the list that reads no settings.
   /// </summary>
   [Fact]
   public async Task JobsKeepPrintingOnTheDeliveredInventoryWhileTheFullDiscoveryFails()
   {
      await using RealtimeRuntimeFixture fixture = new();
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync();
      _ = await fixture.PipeAsync(new PipeRequest("set-printer-profile", PrinterId: "printer", PrinterProfile: new PrinterProfile("Dispatch labels")));
      await fixture.WaitAsync(() => fixture.Count("INVENTORY") >= 2 && fixture.LastClaimWasEmpty);
      int inventories = fixture.Count("INVENTORY");
      fixture.Printer.DiscoveryFailure = () => new TimeoutException(Unreadable);
      int failedFrom = fixture.Printer.Discoveries;

      fixture.AddJob("first");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => string.Equals(fixture.State("first"), "SPOOLED", StringComparison.Ordinal));
      await fixture.WaitAsync(() => fixture.Printer.Discoveries > failedFrom);
      int failedOnce = fixture.Printer.Discoveries;
      fixture.AddJob("second");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => string.Equals(fixture.State("second"), "SPOOLED", StringComparison.Ordinal));
      await fixture.WaitAsync(() => fixture.Printer.Discoveries > failedOnce);
      int failedTwice = fixture.Printer.Discoveries;
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Printer.Discoveries > failedTwice);

      Assert.Equal(2, fixture.Printer.Prints);
      Assert.Equal(2, fixture.Printer.Lookups);
      Assert.InRange(fixture.Printer.Listings, 2, int.MaxValue);
      Assert.Equal(inventories, fixture.Count("INVENTORY"));
      string error = Assert.Single(fixture.Logs, line => line.StartsWith("The printers on this computer could not be read", StringComparison.Ordinal));
      Assert.Equal("The printers on this computer could not be read, so no printer inventory was sent to the server. Jobs are still claimed and printed on the inventory this connection already delivered. "
         + "One printer whose driver does not answer is enough to cause this. Reason: TimeoutException: " + Unreadable, error);
      Assert.DoesNotContain(fixture.Logs, line => line.StartsWith("Realtime connection stopped", StringComparison.Ordinal));
      Assert.False(connection.Completion.IsCompleted);
      Assert.False(fixture.Running.IsCompleted);
      using(JournalStore store = TestState.OpenStore(fixture.DirectoryPath))
      {
         Assert.Equal("Dispatch labels", store.GetJobDetails("first")!.Job.PrinterName);
      }

      fixture.Printer.DiscoveryFailure = null;
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Logs.Any(line => line.StartsWith("The printers on this computer can be read again", StringComparison.Ordinal)));
      AgentLogEntry logged = Assert.Single(fixture.Runtime.GetSnapshot().Logs, entry => entry.Message.StartsWith("The printers on this computer could not be read", StringComparison.Ordinal));
      Assert.Equal("ERROR", logged.Level);
      Assert.Equal(inventories, fixture.Count("INVENTORY"));
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>
   /// The full discovery fails from the start of a connection, so that connection never delivers an inventory. The
   /// connection stays up and the failure is logged with what it means, but no job is claimed: a lane lists the printers
   /// and stops, because dispatch waits for the server to have the inventory. Nothing is sent in place of the inventory.
   /// When the printers can be read again the inventory is delivered and the waiting job is claimed and printed.
   /// </summary>
   [Fact]
   public async Task ConnectionThatHasDeliveredNoInventoryClaimsNothingUntilThePrintersCanBeRead()
   {
      await using RealtimeRuntimeFixture fixture = new();
      fixture.Printer.DiscoveryFailure = () => new IOException(Unreadable);
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync(restricted: true);
      fixture.AddJob("waiting");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Logs.Any(line => line.StartsWith("The printers on this computer could not be read", StringComparison.Ordinal)));
      int listed = fixture.Printer.Listings;
      int failed = fixture.Printer.Discoveries;
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Printer.Listings > listed && fixture.Printer.Discoveries > failed);
      int failedAgain = fixture.Printer.Discoveries;
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Printer.Discoveries > failedAgain);

      Assert.Equal(0, fixture.Claims);
      Assert.Equal(0, fixture.Count("INVENTORY"));
      Assert.Null(fixture.State("waiting"));
      Assert.Equal(0, fixture.Printer.Prints);
      string error = Assert.Single(fixture.Logs, line => line.StartsWith("The printers on this computer could not be read", StringComparison.Ordinal));
      Assert.Equal("The printers on this computer could not be read, so no printer inventory was sent to the server. No job is claimed on this connection until the printers can be read. "
         + "One printer whose driver does not answer is enough to cause this. Reason: IOException: " + Unreadable, error);
      Assert.DoesNotContain(fixture.Logs, line => line.StartsWith("Realtime connection stopped", StringComparison.Ordinal));
      Assert.False(connection.Completion.IsCompleted);
      Assert.False(fixture.Running.IsCompleted);

      fixture.Printer.DiscoveryFailure = null;
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => string.Equals(fixture.State("waiting"), "SPOOLED", StringComparison.Ordinal));
      Assert.Equal(1, fixture.Count("INVENTORY"));
      Assert.Equal(1, fixture.Printer.Prints);
      Assert.Contains(fixture.Logs, line => line.StartsWith("The printers on this computer can be read again", StringComparison.Ordinal));
   }



   /// <summary>
   /// A failure whose reason changes is logged again, once for each reason, so the log follows what is wrong without
   /// repeating it. A read that was cancelled is not a failed read: it is not logged as one and it ends the connection,
   /// as it did before, and the service keeps running.
   /// </summary>
   [Fact]
   public async Task ChangedReasonIsLoggedAgainAndACancelledReadIsNotAFailedRead()
   {
      await using RealtimeRuntimeFixture fixture = new();
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => fixture.Count("INVENTORY") >= 1 && fixture.LastClaimWasEmpty);
      fixture.Printer.DiscoveryFailure = () => new TimeoutException(Unreadable);
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Logs.Any(line => line.EndsWith("Reason: TimeoutException: " + Unreadable, StringComparison.Ordinal)));
      fixture.Printer.DiscoveryFailure = () => new IOException("Synthetic: the printer worker stopped.");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Logs.Any(line => line.EndsWith("Reason: IOException: Synthetic: the printer worker stopped.", StringComparison.Ordinal)));
      Assert.Equal(2, fixture.Logs.Count(line => line.StartsWith("The printers on this computer could not be read", StringComparison.Ordinal)));
      Assert.DoesNotContain(fixture.Logs, line => line.StartsWith("Realtime connection stopped", StringComparison.Ordinal));

      fixture.Printer.DiscoveryFailure = () => new OperationCanceledException("Synthetic: the read was cancelled.");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Logs.Any(line => line.StartsWith("Realtime connection stopped: OperationCanceledException", StringComparison.Ordinal)));
      Assert.Equal(2, fixture.Logs.Count(line => line.StartsWith("The printers on this computer could not be read", StringComparison.Ordinal)));
      Assert.False(fixture.Running.IsCompleted);
   }



   /// <summary>
   /// Cancelling a job that Windows already holds needs the name of the job's queue and nothing else, so it is taken from
   /// the list that reads no settings: the operator's cancel over the pipe and the server's cancel command both reach the
   /// spooler while the full discovery fails, and neither looks a queue up with its media.
   /// </summary>
   [Fact]
   public async Task SpoolCancellationsReachWindowsWhileTheFullDiscoveryFails()
   {
      await using RealtimeRuntimeFixture fixture = new(seed: store => RealtimeRuntimeFixture.SeedSpooledJob(store));
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => fixture.Count("INVENTORY") >= 1 && fixture.LastClaimWasEmpty);
      fixture.Printer.DiscoveryFailure = () => new TimeoutException(Unreadable);

      string reply = await fixture.PipeAsync(new PipeRequest("cancel-job", JobId: "job"));
      Assert.Equal("Cancellation requested from Windows. Pages already sent to the printer may still print; inspect the output.", reply);
      Assert.Equal(1, fixture.Printer.Cancellations);

      await connection.SendAsync("COMMAND", RealtimeRuntimeFixture.Command());
      await fixture.WaitAsync(() => Outcomes(fixture).Length == 1);
      Assert.Equal(["CANCEL_REQUESTED"], Outcomes(fixture), StringComparer.Ordinal);
      Assert.Equal(2, fixture.Printer.Cancellations);
      Assert.Equal(0, fixture.Printer.Lookups);
   }



   /// <summary>
   /// A spooled job whose printer is no longer on the station cannot be cancelled, and each side says so as it always has:
   /// the operator's cancel fails with the words of a search that found nothing, and the server's command is answered
   /// FAILED with the recorded printer unavailable. Windows is not asked to cancel anything.
   /// </summary>
   [Fact]
   public async Task SpoolCancellationOfAJobWhosePrinterIsGoneIsRefusedAsBefore()
   {
      await using RealtimeRuntimeFixture fixture = new(seed: store =>
      {
         store.TrackJob(new PrintJob("job", "removed", "Synthetic job", "PDF", Convert.ToHexString(SHA256.HashData([1, 2, 3])), 3, new PrintOptions(), RealtimeRuntimeFixture.Now(), "CLAIMED"), "Removed queue");
         PrintEvent receipt = new("spooled-job", "SPOOLED", SpoolerJobId: "123");
         store.Record("job", receipt);
         store.Acknowledge(receipt.EventId);
      });
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync();

      string reply = await fixture.PipeAsync(new PipeRequest("cancel-job", JobId: "job"));
      Assert.Equal("Error: " + Assert.Throws<InvalidOperationException>(() => Array.Empty<PrinterInfo>().Single(printer => printer is not null)).Message, reply);

      await connection.SendAsync("COMMAND", RealtimeRuntimeFixture.Command());
      await fixture.WaitAsync(() => Outcomes(fixture).Length == 1);
      Assert.Equal(["FAILED"], Outcomes(fixture), StringComparer.Ordinal);
      using JournalStore inspected = TestState.OpenStore(fixture.DirectoryPath);
      Assert.Equal("The recorded printer is unavailable. No other printer queue was changed.", inspected.GetControlReceipt("command")!.Message);
      Assert.Equal(0, fixture.Printer.Cancellations);
   }



   /// <summary>
   /// A test print needs to know that the printer exists and offers the format, which the list that reads no settings
   /// answers: it is queued on the server while the full discovery fails. A printer that is not on the station, or a
   /// request that names none, is still answered with the instruction to select an available printer, and a format the
   /// printer does not offer with the instruction to share it and choose a supported format.
   /// </summary>
   [Fact]
   public async Task TestPrintIsCheckedAgainstTheListWithoutMedia()
   {
      await using RealtimeRuntimeFixture fixture = new();
      _ = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => fixture.Count("INVENTORY") >= 1 && fixture.LastClaimWasEmpty);
      fixture.Printer.DiscoveryFailure = () => new TimeoutException(Unreadable);

      Assert.StartsWith("Queued on the server", await fixture.PipeAsync(new PipeRequest("test-print", PrinterId: "other", Format: "PDF")), StringComparison.Ordinal);
      Assert.EndsWith("/device/test-print", Assert.Single(fixture.OperatorActions), StringComparison.Ordinal);
      Assert.Equal("Error: Select an available printer.", await fixture.PipeAsync(new PipeRequest("test-print", PrinterId: "removed", Format: "PDF")));
      Assert.Equal("Error: Select an available printer.", await fixture.PipeAsync(new PipeRequest("test-print", Format: "PDF")));
      Assert.StartsWith("Error: Share the printer and select a supported format first.", await fixture.PipeAsync(new PipeRequest("test-print", PrinterId: "other", Format: "XPS")), StringComparison.Ordinal);
      Assert.Single(fixture.OperatorActions);
      Assert.Equal(0, fixture.Printer.Lookups);
   }



   /// <summary>
   /// The pipe requests that read the whole inventory fail while the full discovery fails: the printer list, a sharing
   /// change, and the snapshot and status the companion and the command line ask for, which carry the printer list. Each
   /// is answered with the reason and changes nothing, and the pipe goes on serving. A request that needs no printer is
   /// answered straight afterwards.
   /// </summary>
   [Fact]
   public async Task PipeRequestsThatReadTheInventoryFailWithTheReasonAndThePipeGoesOn()
   {
      await using RealtimeRuntimeFixture fixture = new();
      _ = await fixture.ConnectAsync();
      fixture.Printer.DiscoveryFailure = () => new TimeoutException(Unreadable);

      Assert.Equal("Error: " + Unreadable, await fixture.PipeAsync(new PipeRequest("printers")));
      Assert.Equal("Error: " + Unreadable, await fixture.PipeAsync(new PipeRequest("set-exposure", PrinterId: "other", Exposed: false)));
      Assert.Equal("Error: " + Unreadable, await fixture.PipeAsync(new PipeRequest("snapshot")));
      Assert.Equal("Error: " + Unreadable, await fixture.PipeAsync(new PipeRequest("status")));
      AgentTransportSnapshot transport = JsonSerializer.Deserialize<AgentTransportSnapshot>(await fixture.PipeAsync(new PipeRequest("transport")), PrintApiClient.JsonOptions)!;

      Assert.Equal("CONNECTED", transport.State);
      using(JournalStore store = TestState.OpenStore(fixture.DirectoryPath))
      {
         Assert.Contains("other", store.GetExposedPrinterIds());
      }
      Assert.False(fixture.Running.IsCompleted);
      fixture.Printer.DiscoveryFailure = null;
      Assert.Contains("\"printerId\":\"other\"", await fixture.PipeAsync(new PipeRequest("printers")), StringComparison.Ordinal);
   }



   /// <summary>
   /// The whole path of one job through the service, over the real Windows rules and a Windows that exists only in
   /// memory. The station has three queues: one whose driver stops answering after the station connected, the Zebra queue
   /// that is shared, and a laser queue. A ZPL job for the Zebra queue is claimed, rechecked and spooled. The printers
   /// are asked three times for it (the list before the claim, the lookup of its queue after the download, the print),
   /// and in those three no call names either other queue: the list is the two enumeration calls, the lookup adds the
   /// Zebra queue's settings read, and the print adds that read and the document. Only the inventory report, which still
   /// reads every queue, runs into the queue that does not answer, and it fails without ending the connection.
   /// </summary>
   [Fact]
   public async Task JobIsClaimedRecheckedAndSpooledWhileAnotherQueuesDriverDoesNotAnswer()
   {
      const string zebra = "Synthetic Zebra queue";
      const string zebraId = "6119358ca3e4aafc30d6a3da";
      const string broken = "Synthetic broken queue";
      const string third = "Synthetic laser queue";
      byte[] label = Encoding.ASCII.GetBytes("^XA^FO50,50^FDsynthetic^FS^XZ");
      FakeWindowsPrinting windows = new();
      FakeQueue stuck = windows.Add(new FakeQueue(broken, "Synthetic stuck driver", "192.0.2.20") { MachineDefaults = SyntheticDevMode.Label() }.WithContext(1200, 1800));
      _ = windows.Add(new FakeQueue(zebra, "ZDesigner ZT411-300dpi ZPL", "USB001") { MachineDefaults = SyntheticDevMode.Label() }.WithContext(1200, 1800));
      _ = windows.Add(new FakeQueue(third, "Synthetic laser driver", "192.0.2.10") { MachineDefaults = SyntheticDevMode.Label() }.WithContext(1200, 1800));
      using RecordedPrinters printers = new(new WindowsPrinters(windows, windows, windows, new FakePdfRenderer(windows.Calls)), windows);
      await using RealtimeRuntimeFixture fixture = new(printers: printers, seed: store => store.SetExposure(zebraId, true, printers.Discover()));
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => fixture.Count("INVENTORY") >= 1 && fixture.LastClaimWasEmpty);
      printers.Forget(() => stuck.NeverAnswers = () => new TimeoutException("Synthetic: this queue's driver never answers."));

      fixture.Documents["label"] = () => new MemoryStream(label);
      fixture.Queue.Enqueue(new PrintJob("label", zebraId, "Synthetic label", "ZPL", Convert.ToHexString(SHA256.HashData(label)), label.Length, new PrintOptions(), RealtimeRuntimeFixture.Now(), "QUEUED"));
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => string.Equals(fixture.State("label"), "SPOOLED", StringComparison.Ordinal));
      await fixture.WaitAsync(() => fixture.LastClaimWasEmpty);

      (string Read, string[] Calls, bool Failed)[] reads = printers.Reads();
      string[] enumeration = ["EnumPrintersSize(6)", "EnumPrinters(6, 408)"];
      string[] zebraRead =
      [
         $"OpenPrinter({zebra})", "GetPrinterSize(p1, 8)", "GetPrinter(p1, 8, 232)", "ClosePrinter(p1)",
         $"CreateDC(WINSPOOL, {zebra}, 224 bytes)", "GetDeviceCaps(c1, 88)", "GetDeviceCaps(c1, 90)", "DeleteDC(c1)",
      ];
      (string Read, string[] Calls, bool Failed)[] forTheJob = [.. reads.Where(read => !string.Equals(read.Read, "Discover", StringComparison.Ordinal))];
      Assert.All(forTheJob, read => Assert.False(read.Failed));
      Assert.All(forTheJob, read => Assert.DoesNotContain(read.Calls, call => call.Contains(broken, StringComparison.Ordinal) || call.Contains(third, StringComparison.Ordinal)));
      Assert.Contains(forTheJob, read => string.Equals(read.Read, "ListWithoutMedia", StringComparison.Ordinal));
      Assert.All(forTheJob.Where(read => string.Equals(read.Read, "ListWithoutMedia", StringComparison.Ordinal)), read => Assert.Equal(enumeration, read.Calls, StringComparer.Ordinal));
      Assert.Equal([.. enumeration, .. zebraRead], Assert.Single(forTheJob, read => string.Equals(read.Read, "FindWithMedia " + zebraId, StringComparison.Ordinal)).Calls, StringComparer.Ordinal);
      Assert.Equal(
      [
         .. enumeration, .. zebraRead,
         $"OpenPrinter({zebra})", "StartDocPrinter(p2, QQQ label, RAW)", $"WritePrinter(p2, {label.Length} bytes, {label.Length})", "EndDocPrinter(p2)", "ClosePrinter(p2)",
      ], Assert.Single(forTheJob, read => string.Equals(read.Read, "PrintFileAsync label", StringComparison.Ordinal)).Calls, StringComparer.Ordinal);
      Assert.Equal(forTheJob.Count(read => string.Equals(read.Read, "ListWithoutMedia", StringComparison.Ordinal)) + 2, forTheJob.Length);
      Assert.All(reads.Where(read => string.Equals(read.Read, "Discover", StringComparison.Ordinal)), read => Assert.True(read.Failed));

      FakeSpoolDocument document = Assert.Single(windows.RawDocuments);
      Assert.Equal((zebra, "QQQ label", "RAW", true, false), (document.Queue, document.Name, document.DataType, document.Ended, document.Aborted));
      Assert.Equal(label, document.Bytes.ToArray());
      Assert.DoesNotContain(fixture.Logs, line => line.StartsWith("Realtime connection stopped", StringComparison.Ordinal));
      Assert.False(fixture.Running.IsCompleted);
   }



   /// <summary>The outcome of every command receipt the agent sent, in order.</summary>
   private static string[] Outcomes(RealtimeRuntimeFixture fixture)
      => [.. fixture.Messages.Where(message => message.Type is "COMMAND_RECEIPT").Select(message => message.Payload.GetProperty("receipt").GetProperty("outcome").GetString()!)];
}
