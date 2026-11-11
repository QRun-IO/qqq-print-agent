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



   /// <summary>How the error the service logs when the inventory cannot be read begins, in every case.</summary>
   private const string Unread = "The printer inventory could not be read";



   /// <summary>What that error goes on to say on a connection that has already delivered an inventory. It says the failure stops no job, and does not say that jobs are being claimed, which dispatch being paused can prevent.</summary>
   private const string Delivered = ", so none was sent to the server. This does not stop jobs: they are claimed and printed as before, on the inventory this connection already delivered. ";



   /// <summary>What that error goes on to say on a connection that has delivered no inventory yet.</summary>
   private const string NotDelivered = ", so none was sent to the server. No job is claimed on this connection until the inventory can be read. ";



   /// <summary>How that error ends before the reason: it names both kinds of cause, a printer and the service's own stored data, because both are caught by the one read.</summary>
   private const string Causes = "One printer whose driver does not answer is enough to cause this, and so is a failure to read the service's own stored data. Reason: ";



   /// <summary>How the line the service logs when the inventory can be read again begins.</summary>
   private const string ReadAgain = "The printer inventory can be read again";



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
      string error = Assert.Single(fixture.Logs, line => line.StartsWith(Unread, StringComparison.Ordinal));
      Assert.Equal(Unread + Delivered + Causes + "TimeoutException: " + Unreadable, error);
      Assert.DoesNotContain(fixture.Logs, line => line.StartsWith("Realtime connection stopped", StringComparison.Ordinal));
      Assert.False(connection.Completion.IsCompleted);
      Assert.False(fixture.Running.IsCompleted);
      using(JournalStore store = TestState.OpenStore(fixture.DirectoryPath))
      {
         Assert.Equal("Dispatch labels", store.GetJobDetails("first")!.Job.PrinterName);
      }

      fixture.Printer.DiscoveryFailure = null;
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Logs.Any(line => line.StartsWith(ReadAgain, StringComparison.Ordinal)));
      AgentLogEntry logged = Assert.Single(fixture.Runtime.GetSnapshot().Logs, entry => entry.Message.StartsWith(Unread, StringComparison.Ordinal));
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
      await fixture.WaitAsync(() => fixture.Logs.Any(line => line.StartsWith(Unread, StringComparison.Ordinal)));
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
      string error = Assert.Single(fixture.Logs, line => line.StartsWith(Unread, StringComparison.Ordinal));
      Assert.Equal(Unread + NotDelivered + Causes + "IOException: " + Unreadable, error);
      Assert.DoesNotContain(fixture.Logs, line => line.StartsWith("Realtime connection stopped", StringComparison.Ordinal));
      Assert.False(connection.Completion.IsCompleted);
      Assert.False(fixture.Running.IsCompleted);

      fixture.Printer.DiscoveryFailure = null;
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => string.Equals(fixture.State("waiting"), "SPOOLED", StringComparison.Ordinal));
      Assert.Equal(1, fixture.Count("INVENTORY"));
      Assert.Equal(1, fixture.Printer.Prints);
      Assert.Contains(fixture.Logs, line => line.StartsWith(ReadAgain, StringComparison.Ordinal));
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
      Assert.Equal(2, fixture.Logs.Count(line => line.StartsWith(Unread, StringComparison.Ordinal)));
      Assert.DoesNotContain(fixture.Logs, line => line.StartsWith("Realtime connection stopped", StringComparison.Ordinal));

      fixture.Printer.DiscoveryFailure = () => new OperationCanceledException("Synthetic: the read was cancelled.");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Logs.Any(line => line.StartsWith("Realtime connection stopped: OperationCanceledException", StringComparison.Ordinal)));
      Assert.Equal(2, fixture.Logs.Count(line => line.StartsWith(Unread, StringComparison.Ordinal)));
      Assert.False(fixture.Running.IsCompleted);
   }



   /// <summary>
   /// The error is true while the administrator has paused dispatch. The connection has delivered its inventory, so the
   /// line is the one for that case: it says the failure stops no job and that jobs go on as before. It does not say
   /// that jobs are being claimed, and none is: the job the server holds stays unclaimed because of the pause, as it
   /// would have without the failure.
   /// </summary>
   [Fact]
   public async Task ErrorIsTrueWhileDispatchIsPaused()
   {
      await using RealtimeRuntimeFixture fixture = new();
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync(desiredState: new DeviceDesiredState(1, true, "CONNECTED"));
      await fixture.WaitAsync(() => fixture.Count("INVENTORY") >= 1);
      fixture.Printer.DiscoveryFailure = () => new TimeoutException(Unreadable);
      fixture.AddJob("held");
      await connection.SendAsync("JOB_AVAILABLE", new { });
      await fixture.WaitAsync(() => fixture.Logs.Any(line => line.StartsWith(Unread, StringComparison.Ordinal)));

      string error = Assert.Single(fixture.Logs, line => line.StartsWith(Unread, StringComparison.Ordinal));
      Assert.Equal(Unread + Delivered + Causes + "TimeoutException: " + Unreadable, error);
      Assert.Equal(0, fixture.Claims);
      Assert.Null(fixture.State("held"));
      Assert.Equal(0, fixture.Printer.Prints);
      Assert.False(connection.Completion.IsCompleted);
   }



   /// <summary>
   /// The read that fails is not always a printer's. The same read takes the service's own record of which printers are
   /// shared from the journal, and a failure there is caught and logged by the same line. Here the journal's table of
   /// shared printers is put out of reach while the printers themselves answer. The line gives the journal's failure as
   /// its reason and names the service's stored data as a possible cause, so it does not send the operator looking
   /// only for a driver. The connection is kept, and when the table is back the end of the failure is logged once.
   /// </summary>
   [Fact]
   public async Task FailureToReadTheServicesOwnStoredDataIsLoggedByTheSameLineWithItsReason()
   {
      var clock = new ManualTimeProvider();
      await using RealtimeRuntimeFixture fixture = new(timeProvider: clock);
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => fixture.Count("INVENTORY") >= 1 && fixture.LastClaimWasEmpty);
      MoveSharedPrintersTable(fixture, outOfReach: true);
      await fixture.HeartbeatAsync(clock);
      await fixture.WaitAsync(() => fixture.Logs.Any(line => line.StartsWith(Unread, StringComparison.Ordinal)));

      string error = Assert.Single(fixture.Logs, line => line.StartsWith(Unread, StringComparison.Ordinal));
      Assert.StartsWith(Unread + Delivered + Causes + "SqliteException: ", error, StringComparison.Ordinal);
      Assert.Contains("exposed_printers", error, StringComparison.Ordinal);
      Assert.Null(fixture.Printer.DiscoveryFailure);
      Assert.DoesNotContain(fixture.Logs, line => line.StartsWith("Realtime connection stopped", StringComparison.Ordinal));
      Assert.False(connection.Completion.IsCompleted);

      MoveSharedPrintersTable(fixture, outOfReach: false);
      await fixture.HeartbeatAsync(clock);
      await fixture.WaitAsync(() => fixture.Logs.Any(line => line.StartsWith(ReadAgain, StringComparison.Ordinal)));
      Assert.Single(fixture.Logs, line => line.StartsWith(ReadAgain, StringComparison.Ordinal));
      Assert.Single(fixture.Logs, line => line.StartsWith(Unread, StringComparison.Ordinal));
      Assert.False(fixture.Running.IsCompleted);
   }



   /// <summary>
   /// A round of the synchronizer whose inventory read fails still delivers the receipts that are waiting. The receipt
   /// here is written into the journal while the full discovery fails and no job is offered, so no lane runs (a lane
   /// delivers waiting receipts before it claims, and no claim is made) and only the synchronizer can deliver it. Each
   /// round is set off by a heartbeat on a manual clock. The round after the receipt was written fails its read, sends
   /// nothing in place of the inventory and delivers the receipt once; the server acknowledges it and the journal no
   /// longer holds it as waiting.
   /// </summary>
   [Fact]
   public async Task RoundWhoseInventoryReadFailsStillDeliversTheWaitingReceipt()
   {
      var clock = new ManualTimeProvider();
      await using RealtimeRuntimeFixture fixture = new(timeProvider: clock, seed: store => RealtimeRuntimeFixture.SeedSpooledJob(store));
      ScriptedPrintServer.Connection connection = await fixture.ConnectAsync();
      await fixture.WaitAsync(() => fixture.Count("INVENTORY") >= 1 && fixture.LastClaimWasEmpty);
      fixture.Printer.DiscoveryFailure = () => new TimeoutException(Unreadable);
      int failedFrom = fixture.Printer.Discoveries;
      await fixture.HeartbeatAsync(clock);
      await fixture.WaitAsync(() => fixture.Printer.Discoveries > failedFrom);
      int inventories = fixture.Count("INVENTORY");
      int claims = fixture.Claims;

      using(JournalStore store = TestState.OpenStore(fixture.DirectoryPath))
      {
         store.Record("job", new PrintEvent("waiting-receipt", PrintJobState.SpoolerCompleted));
      }
      await fixture.HeartbeatAsync(clock);
      await fixture.WaitAsync(() => Receipts(fixture, "job", PrintJobState.SpoolerCompleted) > 0);
      await fixture.WaitAsync(() =>
      {
         using JournalStore store = TestState.OpenStore(fixture.DirectoryPath);
         return store.Pending().Count == 0;
      });

      Assert.Equal(1, Receipts(fixture, "job", PrintJobState.SpoolerCompleted));
      Assert.Equal(inventories, fixture.Count("INVENTORY"));
      Assert.Equal(claims, fixture.Claims);
      Assert.Equal(0, fixture.Printer.Prints);
      Assert.DoesNotContain(fixture.Logs, line => line.StartsWith("Realtime connection stopped", StringComparison.Ordinal));
      Assert.False(connection.Completion.IsCompleted);
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



   /// <summary>
   /// Renames the table of shared printers in the running service's journal through a connection of the test's own:
   /// away, which puts it out of the service's reach so that reading the shared printers fails, or back.
   /// </summary>
   private static void MoveSharedPrintersTable(RealtimeRuntimeFixture fixture, bool outOfReach)
   {
      using Microsoft.Data.Sqlite.SqliteConnection journal = TestState.OpenJournal(fixture.DirectoryPath);
      using Microsoft.Data.Sqlite.SqliteCommand rename = journal.CreateCommand();
      rename.CommandText = outOfReach ? "ALTER TABLE exposed_printers RENAME TO exposed_printers_out_of_reach" : "ALTER TABLE exposed_printers_out_of_reach RENAME TO exposed_printers";
      _ = rename.ExecuteNonQuery();
   }



   /// <summary>How many receipts with this status the agent delivered for the job.</summary>
   private static int Receipts(RealtimeRuntimeFixture fixture, string jobId, string status)
      => fixture.Messages.Count(message => message.Type is "JOB_EVENT"
         && string.Equals(message.Payload.GetProperty("jobId").GetString(), jobId, StringComparison.Ordinal)
         && string.Equals(message.Payload.GetProperty("event").GetProperty("status").GetString(), status, StringComparison.Ordinal));



   /// <summary>The outcome of every command receipt the agent sent, in order.</summary>
   private static string[] Outcomes(RealtimeRuntimeFixture fixture)
      => [.. fixture.Messages.Where(message => message.Type is "COMMAND_RECEIPT").Select(message => message.Payload.GetProperty("receipt").GetProperty("outcome").GetString()!)];
}
