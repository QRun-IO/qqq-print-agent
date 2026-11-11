/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The service's side of the print helper boundary, run against the fixture helper (a real process that has no printer):
/// what the service sends, what it makes of every answer a helper can give, and what happens to the helper when the
/// caller gives up. The fixture either runs the real helper loop over a fake backend and a fake spooler, or answers with
/// text the test dictates.
/// </summary>
public sealed class IsolatedPrinterBackendTests
{
   /// <summary>A queue identifier in the form the lookup sends: 24 hexadecimal digits in lower case.</summary>
   private const string QueueId = "72a900462ca6a6e18ecb8fb2";



   /// <summary>A synthetic job; the fixture helper never opens its document.</summary>
   private static readonly PrintJob Job = new("job-1", "fixture", "Synthetic", "PDF", new string('0', 64), 1, new PrintOptions(), "2026-11-01T00:00:00Z", "QUEUED");



   /// <summary>The fixture helper's executable, which the build puts beside the tests; it has no printer and never prints.</summary>
   private static string FixtureExecutable => Path.ChangeExtension(typeof(WorkerFixture.Program).Assembly.Location, ".exe");



   /// <summary>
   /// A print is one helper started with --printer-worker and this process as its parent, sent one JSON line that names the
   /// operation, the job and the absolute path of the staged file (never its content). The helper's receipt is returned.
   /// </summary>
   [Fact]
   public async Task PrintSendsThePathOfTheStagedFileAndReturnsTheReceipt()
   {
      string record = Path.Combine(Path.GetTempPath(), "qqq-helper-request-" + Guid.NewGuid().ToString("N") + ".txt");
      try
      {
         IsolatedPrinterBackend backend = Backend("--record", record, "--respond", "{\"success\":true,\"spoolerJobId\":\"4242\"}");

         string receipt = await backend.PrintFileAsync(Job, Path.Combine("staging", "synthetic.bin"), TimeSpan.FromSeconds(30), CancellationToken.None);

         Assert.Equal("4242", receipt);
         string[] recorded = await File.ReadAllLinesAsync(record);
         Assert.Equal("--printer-worker --record " + record, recorded[0]);
         Assert.Equal(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), recorded[1]);
         PrinterWorkerHost.Request request = Assert.IsType<PrinterWorkerHost.Request>(JsonSerializer.Deserialize<PrinterWorkerHost.Request>(recorded[2], PrintApiClient.JsonOptions));
         Assert.Equal(("print-file", Job, Path.GetFullPath(Path.Combine("staging", "synthetic.bin"))), (request.Operation, request.Job, request.ContentPath));
         Assert.Equal((null, null, null), (request.PrinterName, request.JobId, request.SpoolerJobId));
      }
      finally
      {
         File.Delete(record);
      }
   }



   /// <summary>An answer that claims success but carries no receipt, or no inventory, is refused; the service never invents one.</summary>
   [Fact]
   public async Task SuccessWithoutAReceiptOrAnInventoryIsRefused()
   {
      IsolatedPrinterBackend backend = Backend("--respond", "{\"success\":true}");
      InvalidDataException noReceipt = await Assert.ThrowsAsync<InvalidDataException>(() => backend.PrintFileAsync(Job, "synthetic.bin", TimeSpan.FromSeconds(30), CancellationToken.None));
      Assert.Equal("Printer worker returned no spooler receipt.", noReceipt.Message);
      InvalidDataException noInventory = Assert.Throws<InvalidDataException>(() => backend.Discover());
      Assert.Equal("Printer worker returned no inventory.", noInventory.Message);
   }



   /// <summary>
   /// The list without media and the lookup of one queue are each one helper, started as a print's helper is, sent one JSON
   /// line that names the operation and, for the lookup, the identifier of the queue. Nothing else travels: no job, no
   /// queue name, no path.
   /// </summary>
   [Theory]
   [InlineData("list-without-media")]
   [InlineData("find-with-media")]
   public async Task ListAndLookupEachSendOneRequestThatCarriesOnlyWhatItNeeds(string operation)
   {
      bool lookup = string.Equals(operation, "find-with-media", StringComparison.Ordinal);
      string record = Path.Combine(Path.GetTempPath(), "qqq-helper-request-" + Guid.NewGuid().ToString("N") + ".txt");
      try
      {
         IsolatedPrinterBackend backend = Backend("--record", record, "--respond", "{\"success\":true,\"printers\":[]}");

         if(lookup)
         {
            Assert.Null(backend.FindWithMedia(QueueId));
         }
         else
         {
            Assert.Empty(backend.ListWithoutMedia());
         }

         string[] recorded = await File.ReadAllLinesAsync(record);
         Assert.Equal("--printer-worker --record " + record, recorded[0]);
         Assert.Equal(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), recorded[1]);
         Assert.Equal(lookup ? "{\"operation\":\"find-with-media\",\"printerId\":\"" + QueueId + "\"}" : "{\"operation\":\"list-without-media\"}", recorded[2]);
      }
      finally
      {
         File.Delete(record);
      }
   }



   /// <summary>The lookup returns the one queue the helper answered with, as it answered it, media profile included.</summary>
   [Fact]
   public void LookupReturnsTheQueueTheHelperFound()
   {
      PrinterMediaProfile profile = new("default-id", new string('a', 64), "Default", 101600, 152400, 300, 300);
      PrinterInfo queue = new(QueueId, "Synthetic queue", "Fixture", "None", "READY", ["PDF"], MediaProfiles: [profile]);

      PrinterInfo found = Assert.IsType<PrinterInfo>(Backend("--respond", Answer(queue)).FindWithMedia(QueueId));

      Assert.Equal((QueueId, "Synthetic queue", "Fixture", "None", "READY"), (found.PrinterId, found.Name, found.DriverName, found.PortName, found.Status));
      Assert.Equal(["PDF"], found.Formats, StringComparer.Ordinal);
      Assert.Equal(profile, Assert.Single(found.MediaProfiles!));
   }



   /// <summary>
   /// An answer to the lookup that holds a queue other than the one asked for, or more than one queue, is refused: the
   /// service asked about one queue and takes nothing else for it. An answer to the list or the lookup that claims
   /// success and carries no list is refused as discovery's is.
   /// </summary>
   [Fact]
   public void AnswerThatIsNotWhatWasAskedForIsRefused()
   {
      PrinterInfo asked = new(QueueId, "Synthetic queue", "Fixture", "None", "READY", ["PDF"]);
      PrinterInfo other = asked with { PrinterId = "000000000000000000000000" };

      InvalidDataException another = Assert.Throws<InvalidDataException>(() => Backend("--respond", Answer(other)).FindWithMedia(QueueId));
      Assert.Equal("Printer worker returned a printer that was not asked for.", another.Message);
      InvalidDataException several = Assert.Throws<InvalidDataException>(() => Backend("--respond", Answer(asked, asked)).FindWithMedia(QueueId));
      Assert.Equal("Printer worker returned a printer that was not asked for.", several.Message);
      InvalidDataException noQueue = Assert.Throws<InvalidDataException>(() => Backend("--respond", "{\"success\":true}").FindWithMedia(QueueId));
      Assert.Equal("Printer worker returned no inventory.", noQueue.Message);
      InvalidDataException noList = Assert.Throws<InvalidDataException>(() => Backend("--respond", "{\"success\":true}").ListWithoutMedia());
      Assert.Equal("Printer worker returned no inventory.", noList.Message);
   }



   /// <summary>A failure the helper reports for the list or the lookup arrives as it does for discovery: an I/O failure carrying the helper's message.</summary>
   [Fact]
   public void HelperFailureOfTheListOrTheLookupIsAnIoFailureWithTheHelpersMessage()
   {
      const string answer = "{\"success\":false,\"error\":\"Synthetic failure.\"}";
      IOException list = Assert.Throws<IOException>(() => Backend("--respond", answer, "--exit-code", "1").ListWithoutMedia());
      Assert.Equal("Synthetic failure.", list.Message);
      IOException lookup = Assert.Throws<IOException>(() => Backend("--respond", answer, "--exit-code", "1").FindWithMedia(QueueId));
      Assert.Equal("Synthetic failure.", lookup.Message);
   }



   /// <summary>
   /// Text that does not have the form of a queue identifier names no queue: the lookup answers that there is none and
   /// starts no helper for it, so nothing but 24 hexadecimal digits is ever put in a request.
   /// </summary>
   [Theory]
   [InlineData("")]
   [InlineData("fixture")]
   [InlineData("Synthetic queue")]
   [InlineData("72A900462CA6A6E18ECB8FB2")]
   [InlineData("72a900462ca6a6e18ecb8fb20")]
   public void LookupOfTextThatIsNotAQueueIdentifierIsAbsentAndStartsNoHelper(string text)
   {
      int launches = 0;
      IsolatedPrinterBackend backend = new(FixtureExecutable)
      {
         StartProcess = start =>
         {
            launches++;
            return Process.Start(start) ?? throw new InvalidOperationException("Fixture launch failed.");
         },
      };
      Assert.Null(backend.FindWithMedia(text));
      Assert.Equal(0, launches);
   }



   /// <summary>
   /// A cancellation goes through the helper's own ownership rule (here over the fixture's fake spooler, which holds job 7
   /// named "QQQ fixture-job"): the job that is ours is cancelled, a number that belongs to another document is refused as
   /// an ownership mismatch and a job that is gone is reported as already absent, each as the same refusal on this side.
   /// </summary>
   [Fact]
   public async Task CancellationCrossesTheBoundaryWithItsRefusals()
   {
      IsolatedPrinterBackend backend = Backend();
      await backend.CancelAsync("Synthetic queue", "fixture-job", "7");

      SpoolCancellationException mismatch = await Assert.ThrowsAsync<SpoolCancellationException>(() => backend.CancelAsync("Synthetic queue", "another-job", "7"));
      Assert.Equal(("OWNERSHIP_MISMATCH", "The spooler number now belongs to another document; cancellation was refused."), (mismatch.Outcome, mismatch.Message));

      SpoolCancellationException absent = await Assert.ThrowsAsync<SpoolCancellationException>(() => backend.CancelAsync("Synthetic queue", "fixture-job", "8"));
      Assert.Equal(("ALREADY_ABSENT", "The job is no longer in the Windows queue. Pages already printed cannot be recalled."), (absent.Outcome, absent.Message));
   }



   /// <summary>A failure the helper reports without a known code arrives as an I/O failure carrying the helper's message, which the worker treats as an uncertain outcome.</summary>
   [Fact]
   public async Task UnknownHelperFailureIsAnIoFailureWithTheHelpersMessage()
   {
      IOException failure = await Assert.ThrowsAsync<IOException>(() => Backend().PrintFileAsync(Job, "synthetic.bin", TimeSpan.FromSeconds(30), CancellationToken.None));
      Assert.Equal("This fixture never opens documents or prints.", failure.Message);
   }



   /// <summary>
   /// Each failure answer becomes the exception its code stands for, and an answer without a message gets a fixed one: a
   /// media mismatch, a refused spool cancellation, or, for no code or one this side does not know, an I/O failure.
   /// </summary>
   [Theory]
   [InlineData("{\"success\":false}", typeof(IOException), "The printer worker stopped unexpectedly. Inspect the printer before retrying.")]
   [InlineData("{\"success\":false,\"errorCode\":\"MEDIA_PROFILE_MISMATCH\"}", typeof(PrinterMediaMismatchException), "The selected printer default changed before submission.")]
   [InlineData("{\"success\":false,\"errorCode\":\"ALREADY_ABSENT\"}", typeof(SpoolCancellationException), "Verified spool cancellation was refused.")]
   [InlineData("{\"success\":false,\"errorCode\":\"OWNERSHIP_MISMATCH\"}", typeof(SpoolCancellationException), "Verified spool cancellation was refused.")]
   [InlineData("{\"success\":false,\"errorCode\":\"SOMETHING_NEW\",\"error\":\"Synthetic failure.\"}", typeof(IOException), "Synthetic failure.")]
   [InlineData("{\"success\":false,\"errorCode\":\"media_profile_mismatch\",\"error\":\"Synthetic failure.\"}", typeof(IOException), "Synthetic failure.")]
   public async Task FailureAnswerBecomesTheExceptionItsCodeStandsFor(string answer, Type expected, string message)
   {
      Exception failure = await Assert.ThrowsAsync(expected, () => Backend("--respond", answer, "--exit-code", "1").PrintFileAsync(Job, "synthetic.bin", TimeSpan.FromSeconds(30), CancellationToken.None));
      Assert.Equal(message, failure.Message);
   }



   /// <summary>An answer that claims success from a helper that exited with an error is not trusted: it is an I/O failure, so the outcome stays uncertain.</summary>
   [Fact]
   public async Task SuccessFromAHelperThatExitedWithAnErrorIsNotTrusted()
   {
      IsolatedPrinterBackend backend = Backend("--respond", "{\"success\":true,\"spoolerJobId\":\"4242\"}", "--exit-code", "3");
      IOException failure = await Assert.ThrowsAsync<IOException>(() => backend.PrintFileAsync(Job, "synthetic.bin", TimeSpan.FromSeconds(30), CancellationToken.None));
      Assert.Equal("The printer worker stopped unexpectedly. Inspect the printer before retrying.", failure.Message);
   }



   /// <summary>An answer longer than 1 MiB is refused instead of parsed.</summary>
   [Fact]
   public async Task OversizedAnswerIsRefused()
   {
      InvalidDataException refusal = await Assert.ThrowsAsync<InvalidDataException>(() => Backend("--flood").PrintFileAsync(Job, "synthetic.bin", TimeSpan.FromSeconds(30), CancellationToken.None));
      Assert.Equal("Printer worker response exceeded its limit.", refusal.Message);
   }



   /// <summary>An answer that is the JSON word null is no response, and one that is not JSON fails as invalid JSON; neither is taken for success.</summary>
   [Fact]
   public async Task AnswerThatIsNotAResponseIsRefused()
   {
      InvalidDataException none = await Assert.ThrowsAsync<InvalidDataException>(() => Backend("--respond", "null").PrintFileAsync(Job, "synthetic.bin", TimeSpan.FromSeconds(30), CancellationToken.None));
      Assert.Equal("Printer worker returned no response.", none.Message);
      await Assert.ThrowsAsync<JsonException>(() => Backend("--respond", "READY").PrintFileAsync(Job, "synthetic.bin", TimeSpan.FromSeconds(30), CancellationToken.None));
   }



   /// <summary>A caller that has already given up starts no helper at all.</summary>
   [Fact]
   public async Task CallerThatAlreadyGaveUpStartsNoHelper()
   {
      int launches = 0;
      IsolatedPrinterBackend backend = new(FixtureExecutable)
      {
         StartProcess = start =>
         {
            launches++;
            return Process.Start(start) ?? throw new InvalidOperationException("Fixture launch failed.");
         },
      };
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.PrintFileAsync(Job, "synthetic.bin", TimeSpan.FromSeconds(30), new CancellationToken(canceled: true)));
      Assert.Equal(0, launches);
   }



   /// <summary>When the caller gives up while the helper is stuck, the call ends as a cancellation (not as a timeout to inspect) and the stuck helper is killed.</summary>
   [Fact]
   public async Task CallerGivingUpIsACancellationAndTheStuckHelperIsKilled()
   {
      int helperId = 0;
      IsolatedPrinterBackend backend = new(FixtureExecutable)
      {
         StartProcess = start =>
         {
            start.ArgumentList.Add("--hang");
            Process helper = Process.Start(start) ?? throw new InvalidOperationException("Fixture launch failed.");
            helperId = helper.Id;
            return helper;
         },
      };
      using CancellationTokenSource caller = new(TimeSpan.FromMilliseconds(500));

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.PrintFileAsync(Job, "synthetic.bin", TimeSpan.FromMinutes(2), caller.Token));

      Assert.True(HelperIsGone(helperId));
   }



   /// <summary>The answer a helper gives when a read succeeded with these queues, as one JSON line.</summary>
   private static string Answer(params PrinterInfo[] queues) => JsonSerializer.Serialize(new PrinterWorkerHost.Response(true, queues), PrintApiClient.JsonOptions);



   /// <summary>A backend whose helper is the fixture, started with the given extra arguments.</summary>
   private static IsolatedPrinterBackend Backend(params string[] arguments) => new(FixtureExecutable)
   {
      StartProcess = start =>
      {
         foreach(string argument in arguments)
         {
            start.ArgumentList.Add(argument);
         }
         return Process.Start(start) ?? throw new InvalidOperationException("Fixture launch failed.");
      },
   };



   /// <summary>True when the process has exited or no longer exists.</summary>
   private static bool HelperIsGone(int processId)
   {
      try
      {
         using var process = Process.GetProcessById(processId);
         return process.HasExited;
      }
      catch(ArgumentException)
      {
         return true;
      }
   }
}
