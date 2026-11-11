/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The print helper's one-operation loop, run inside the test process with its request and answer as strings, a fake
/// printer backend, a fake spooler and a parent watch that watches nothing. It shows what the helper refuses, what each
/// operation is handed, how a failure is reported to the service, and that the parent is watched for exactly as long as
/// the operation runs. No second process is started and no printer is touched.
/// </summary>
public sealed class PrinterWorkerHostTests
{
   private const string Queue = "Synthetic label queue";

   private static readonly PrintJob Job = new("job-1", "printer-1", "Synthetic", "PDF", new string('0', 64), 1, new PrintOptions(), "2026-11-01T00:00:00Z", "QUEUED");



   /// <summary>A helper that was not given a plain process id for its parent refuses before it starts a watch or reads a single character of its request.</summary>
   [Theory]
   [InlineData(null)]
   [InlineData("")]
   [InlineData("service")]
   [InlineData("-42")]
   [InlineData(" 42")]
   [InlineData("4.2")]
   public async Task HelperWithoutAParentRefusesBeforeItReadsItsRequest(string? parent)
   {
      FakeParentWatch watch = new();
      using StringReader input = new("{\"operation\":\"discover\"}\n");
      using StringWriter output = new();
      RecordingPrinters printers = new();

      int exit = await PrinterWorkerHost.RunAsync(printers, new FakeSpoolerJobs(), watch, parent, input, output);

      Assert.Equal(1, exit);
      PrinterWorkerHost.Response response = Read(output);
      Assert.False(response.Success);
      Assert.Equal("Printer workers must be launched by the print connector.", response.Error);
      Assert.Null(watch.ParentId);
      Assert.Equal('{', input.Peek());
      Assert.Equal(0, printers.Discoveries);
   }



   /// <summary>Discover answers with the backend's inventory and exit code 0; the parent named in the environment is watched while the backend runs and the watch is stopped before the helper returns.</summary>
   [Fact]
   public async Task DiscoverReturnsTheInventoryWhileTheParentIsWatched()
   {
      FakeParentWatch watch = new();
      bool watchedDuringTheCall = false;
      RecordingPrinters printers = new()
      {
         OnDiscover = () =>
         {
            watchedDuringTheCall = watch.Watching;
            return [new PrinterInfo("printer-1", Queue, "Synthetic label driver", "USB001", "READY", ["PDF"])];
         },
      };

      (int exit, PrinterWorkerHost.Response response) = await RunAsync(new PrinterWorkerHost.Request("discover"), printers, watch: watch);

      Assert.Equal(0, exit);
      Assert.True(response.Success);
      Assert.Equal("printer-1", Assert.Single(response.Printers!).PrinterId);
      Assert.Equal(4242, watch.ParentId);
      Assert.True(watchedDuringTheCall);
      Assert.True(watch.Stopped);
   }



   /// <summary>
   /// Print-file hands the backend the job and the path as they came, with no time limit of its own (the service that
   /// started the helper holds the limit and ends the process), and a token that is live during the call. The answer
   /// carries the spooler receipt.
   /// </summary>
   [Fact]
   public async Task PrintFileHandsOverTheJobAndPathWithNoLimitOfItsOwn()
   {
      (PrintJob Job, string Path, TimeSpan Limit, bool Cancelled)? seen = null;
      CancellationToken lifetime = CancellationToken.None;
      RecordingPrinters printers = new()
      {
         OnPrint = (job, path, limit, token) =>
         {
            seen = (job, path, limit, token.IsCancellationRequested);
            lifetime = token;
            return Task.FromResult("4101");
         },
      };

      (int exit, PrinterWorkerHost.Response response) = await RunAsync(new PrinterWorkerHost.Request("print-file", Job, ContentPath: @"C:\staging\synthetic.bin"), printers);

      Assert.Equal(0, exit);
      Assert.Equal((true, "4101"), (response.Success, response.SpoolerJobId));
      Assert.Equal((Job.JobId, @"C:\staging\synthetic.bin", Timeout.InfiniteTimeSpan, false), (seen!.Value.Job.JobId, seen.Value.Path, seen.Value.Limit, seen.Value.Cancelled));
      Assert.True(lifetime.IsCancellationRequested);
   }



   /// <summary>Print-file without a job, or without the path of a verified file, is refused and the backend is never called.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task PrintFileNeedsBothAJobAndAVerifiedFile(bool withJob)
   {
      RecordingPrinters printers = new();
      PrinterWorkerHost.Request request = withJob ? new PrinterWorkerHost.Request("print-file", Job) : new PrinterWorkerHost.Request("print-file", ContentPath: @"C:\staging\synthetic.bin");
      (int exit, PrinterWorkerHost.Response response) = await RunAsync(request, printers);
      Assert.Equal(1, exit);
      Assert.Equal((false, "The print worker requires a verified document file.", null), (response.Success, response.Error, response.ErrorCode));
      Assert.Equal(0, printers.Prints);
   }



   /// <summary>Cancel runs the ownership rule against the spooler it was given: the job is read back and, being ours, deleted.</summary>
   [Fact]
   public async Task CancelDeletesTheJobThatIsOurs()
   {
      FakeSpoolerJobs spooler = new FakeSpoolerJobs().WithJob(Queue, 42, "QQQ job-1");
      (int exit, PrinterWorkerHost.Response response) = await RunAsync(new PrinterWorkerHost.Request("cancel", PrinterName: Queue, JobId: "job-1", SpoolerJobId: "42"), spooler: spooler);
      Assert.Equal(0, exit);
      Assert.Equal((true, null, null), (response.Success, response.Error, response.ErrorCode));
      Assert.Equal("SetJob(0x101, 42, 5)", spooler.Calls[^2]);
      Assert.Empty(spooler.Queues[Queue]);
   }



   /// <summary>
   /// A refused cancellation leaves the helper with exit code 1 and the refusal's own code, which the service turns back
   /// into the same refusal: another document's number is an ownership mismatch, a job that left the queue is already
   /// absent. A request that names no receipt at all is an ordinary failure without a code.
   /// </summary>
   [Theory]
   [InlineData("job-other", "42", "OWNERSHIP_MISMATCH", "The spooler number now belongs to another document; cancellation was refused.")]
   [InlineData("job-1", "43", "ALREADY_ABSENT", "The job is no longer in the Windows queue. Pages already printed cannot be recalled.")]
   [InlineData("job-1", null, null, "This job has no valid Windows spooler receipt.")]
   public async Task RefusedCancellationKeepsItsCode(string jobId, string? receipt, string? code, string message)
   {
      FakeSpoolerJobs spooler = new FakeSpoolerJobs().WithJob(Queue, 42, "QQQ job-1");
      (int exit, PrinterWorkerHost.Response response) = await RunAsync(new PrinterWorkerHost.Request("cancel", PrinterName: Queue, JobId: jobId, SpoolerJobId: receipt), spooler: spooler);
      Assert.Equal(1, exit);
      Assert.Equal((false, message, code), (response.Success, response.Error, response.ErrorCode));
      Assert.True(spooler.Queues[Queue].ContainsKey(42));
      Assert.Equal(0, spooler.OpenHandles);
   }



   /// <summary>A media refusal made by the backend is reported with the media code, so the service records a known refusal with nothing submitted and not an uncertain outcome.</summary>
   [Fact]
   public async Task MediaRefusalKeepsItsCode()
   {
      RecordingPrinters printers = new() { OnPrint = (_, _, _, _) => throw new PrinterMediaMismatchException("The selected printer media profile changed or is unavailable.") };
      (int exit, PrinterWorkerHost.Response response) = await RunAsync(new PrinterWorkerHost.Request("print-file", Job, ContentPath: @"C:\staging\synthetic.bin"), printers);
      Assert.Equal(1, exit);
      Assert.Equal((false, "The selected printer media profile changed or is unavailable.", "MEDIA_PROFILE_MISMATCH"), (response.Success, response.Error, response.ErrorCode));
   }



   /// <summary>Any other failure is reported with its message, with credential-shaped text redacted before it leaves the process, and no code.</summary>
   [Fact]
   public async Task OtherFailuresAreReportedRedactedAndWithoutACode()
   {
      RecordingPrinters printers = new() { OnDiscover = () => throw new InvalidOperationException("The driver answered: Bearer synthetic-token-value") };
      (int exit, PrinterWorkerHost.Response response) = await RunAsync(new PrinterWorkerHost.Request("discover"), printers);
      Assert.Equal(1, exit);
      Assert.Equal((false, "The driver answered: Bearer [REDACTED]", null), (response.Success, response.Error, response.ErrorCode));
   }



   /// <summary>A request that is absent, is the JSON word null, names an operation the helper does not have, or is not JSON at all is answered with a failure and exit code 1; the watch is stopped all the same.</summary>
   [Theory]
   [InlineData("", "Invalid printer worker request size.")]
   [InlineData("null\n", "Missing printer worker request.")]
   [InlineData("{\"operation\":\"print\"}\n", "Unknown printer worker operation.")]
   [InlineData("{\"operation\":\"DISCOVER\"}\n", "Unknown printer worker operation.")]
   [InlineData("{\"operation\":\n", null)]
   public async Task UnusableRequestIsAnsweredWithAFailure(string text, string? message)
   {
      FakeParentWatch watch = new();
      using StringReader input = new(text);
      using StringWriter output = new();
      RecordingPrinters printers = new();

      int exit = await PrinterWorkerHost.RunAsync(printers, new FakeSpoolerJobs(), watch, "4242", input, output);

      Assert.Equal(1, exit);
      PrinterWorkerHost.Response response = Read(output);
      Assert.False(response.Success);
      Assert.NotNull(response.Error);
      if(message is not null)
      {
         Assert.Equal(message, response.Error);
      }
      Assert.Equal((0, 0), (printers.Discoveries, printers.Prints));
      Assert.True(watch.Stopped);
   }



   /// <summary>
   /// A watch that fails instead of watching does not stop the helper: the operation is carried out with nobody watching
   /// the parent and its answer is written, and only then, when the helper waits for the watch to stop, does the watch's
   /// failure come out, in place of the exit code. This is what the helper did before the watch was moved behind
   /// <see cref="IParentWatch"/>; the test records it, so a change to it is a decision and not an accident.
   /// </summary>
   [Fact]
   public async Task WatchThatFailsLeavesTheOperationUnwatchedAndSurfacesWhenTheHelperEnds()
   {
      InvalidOperationException fault = new("Synthetic: the parent could not be waited for.");
      using StringReader input = new("{\"operation\":\"discover\"}\n");
      using StringWriter output = new();
      RecordingPrinters printers = new();

      InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => PrinterWorkerHost.RunAsync(printers, new FakeSpoolerJobs(), new FailingWatch(fault), "4242", input, output));

      Assert.Same(fault, thrown);
      Assert.Equal(1, printers.Discoveries);
      Assert.True(Read(output).Success);
   }



   /// <summary>
   /// The entry point the service's executable calls takes the parent's process id from the environment. In a process that
   /// was not started as a helper there is none, so it refuses with exit code 1 before it reads the console or asks Windows
   /// anything; this is the only path of it a test may run.
   /// </summary>
   [Fact]
   public async Task RealEntryPointRefusesToRunOutsideAHelperProcess()
   {
      Assert.Null(Environment.GetEnvironmentVariable(PrinterWorkerHost.ParentEnvironmentVariable));
      Assert.Equal(1, await PrinterWorkerHost.RunAsync());
   }



   /// <summary>Runs the helper loop on one request with the given fakes, parented to process 4242, and returns its exit code and its single answer.</summary>
   private static async Task<(int Exit, PrinterWorkerHost.Response Response)> RunAsync(PrinterWorkerHost.Request request, RecordingPrinters? printers = null, FakeSpoolerJobs? spooler = null, FakeParentWatch? watch = null)
   {
      using StringReader input = new(JsonSerializer.Serialize(request, PrintApiClient.JsonOptions) + "\n");
      using StringWriter output = new();
      int exit = await PrinterWorkerHost.RunAsync(printers ?? new RecordingPrinters(), spooler ?? new FakeSpoolerJobs(), watch ?? new FakeParentWatch(), "4242", input, output);
      return (exit, Read(output));
   }



   /// <summary>Parses the helper's answer, which must be exactly one line.</summary>
   private static PrinterWorkerHost.Response Read(StringWriter output)
   {
      string line = Assert.Single(output.ToString().Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries));
      return Assert.IsType<PrinterWorkerHost.Response>(JsonSerializer.Deserialize<PrinterWorkerHost.Response>(line, PrintApiClient.JsonOptions));
   }



   /// <summary>A printer backend that does what a test tells it and counts how often it was asked; it has no printer.</summary>
   private sealed class RecordingPrinters : IPrinterBackend
   {
      /// <summary>What discovery returns or throws; an empty inventory unless a test says otherwise.</summary>
      internal Func<IReadOnlyList<PrinterInfo>> OnDiscover { get; init; } = () => [];



      /// <summary>What printing a file returns or throws; a fixed receipt unless a test says otherwise.</summary>
      internal Func<PrintJob, string, TimeSpan, CancellationToken, Task<string>> OnPrint { get; init; } = (_, _, _, _) => Task.FromResult("1");



      /// <summary>How often discovery was asked for.</summary>
      internal int Discoveries { get; private set; }



      /// <summary>How often a file was handed over.</summary>
      internal int Prints { get; private set; }



      /// <summary>Counts the call and does what the test said.</summary>
      public IReadOnlyList<PrinterInfo> Discover()
      {
         Discoveries++;
         return OnDiscover();
      }



      /// <summary>Counts the call and does what the test said.</summary>
      public Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken)
      {
         Prints++;
         return OnPrint(job, path, submissionTimeout, cancellationToken);
      }
   }



   /// <summary>A parent watch whose wait has already failed when it is started, as a wait on a process that cannot be waited for would; it watches nothing and ends no process.</summary>
   private sealed class FailingWatch(Exception fault) : IParentWatch
   {
      /// <summary>Returns a task that has already failed with the given exception.</summary>
      public Task WatchAsync(int parentId, CancellationToken token) => Task.FromException(fault);
   }
}
