/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;


namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Covers jobs the server has queued for this device but that have produced no local receipt yet.</summary>
public sealed class AgentRuntimeQueuedJobsTests
{
   /// <summary>
   /// A job known only from the server's queue must show in the snapshot, in a name-sorted page and in job details (with no
   /// events), yet leave nothing in the local journal. Cancelling it asks the server, removes it from the view, and must never
   /// reach the printer: the fake backend throws if a print is attempted, and the test also asserts zero attempts.
   /// </summary>
   [Fact]
   public async Task QueuedJobAppearsAndCanBeCanceledBeforeAnyLocalReceipt()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-queued-" + Guid.NewGuid().ToString("N"));
      string pipeName = "QqqQueued-test-" + Guid.NewGuid().ToString("N");
      using CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
      ServerState server = new();
      NoPrintBackend printers = new();
      AgentRuntime? runtime = null;
      Task? running = null;
      try
      {
         using(AgentStore store = new(directory))
         {
            store.SaveConfiguration(new AgentConfiguration("https://example.invalid", "device", "synthetic-token"));
            store.SavePrinterProfile("printer", new PrinterProfile("Dispatch labels"));
         }
         runtime = new AgentRuntime(directory, _ => { }, printers,
            configuration => new PrintApiClient(configuration.ServerUrl, configuration.DeviceToken, new Handler(server)),
            pipeName, () => new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly));
         running = runtime.RunAsync(stop.Token);
         while(runtime.GetSnapshot().Jobs.Count == 0)
         {
            await Task.Delay(10, stop.Token);
         }
         JobSnapshot queued = Assert.Single(runtime.GetSnapshot().Jobs);
         Assert.Equal("queued-job", queued.JobId);
         Assert.Equal("QUEUED", queued.Status);
         Assert.Equal("Synthetic queued document", queued.Title);
         Assert.Equal("Dispatch labels", queued.PrinterName);
         await VerifyPagedPrinterNameAsync(pipeName, stop.Token);
         using(AgentStore store = new(directory))
         {
            Assert.Null(store.GetJobDetails(queued.JobId));
         }
         await VerifyQueuedDetailsAsync(pipeName, queued.JobId, stop.Token);
         string result = await SendAsync(pipeName, new PipeRequest("cancel-job", JobId: queued.JobId), stop.Token);
         Assert.StartsWith("The server accepted the cancellation before dispatch.", result, StringComparison.Ordinal);
         Assert.Equal(1, Volatile.Read(ref server.Cancellations));
         Assert.Empty(runtime.GetSnapshot().Jobs);
         Assert.Equal(0, printers.PrintAttempts);
         using(AgentStore store = new(directory))
         {
            Assert.Null(store.GetJobDetails(queued.JobId));
         }
      }
      finally
      {
         await stop.CancelAsync();
         if(running is not null)
         {
            try
            { await running; }
            catch(OperationCanceledException) { }
         }
         runtime?.Dispose();
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>A queued job's details come from the queue snapshot alone: status QUEUED and an empty event history.</summary>
   private static async Task VerifyQueuedDetailsAsync(string pipeName, string jobId, CancellationToken token)
   {
      string text = await SendAsync(pipeName, new PipeRequest("job-details", JobId: jobId), token);
      JobDetailsSnapshot details = JsonSerializer.Deserialize<JobDetailsSnapshot>(text, PrintApiClient.JsonOptions)!;
      Assert.Equal("QUEUED", details.Job.Status);
      Assert.Empty(details.Events);
   }



   /// <summary>Sorting by printer name must carry the operator-chosen display name onto queued jobs, which have no local row to take it from.</summary>
   private static async Task VerifyPagedPrinterNameAsync(string pipeName, CancellationToken token)
   {
      string text = await SendAsync(pipeName, new PipeRequest("query-jobs", JobQuery: new JobQuery(SortBy: "printerName", Descending: false)), token);
      JobPage page = JsonSerializer.Deserialize<JobPage>(text, PrintApiClient.JsonOptions)!;
      Assert.Equal("Dispatch labels", Assert.Single(page.Jobs).PrinterName);
   }



   /// <summary>Sends one line-delimited JSON request over the runtime's private test pipe and returns the one-line reply.</summary>
   private static async Task<string> SendAsync(string name, PipeRequest request, CancellationToken token)
   {
      await using NamedPipeClientStream pipe = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
      await pipe.ConnectAsync(token);
      using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };
      using StreamReader reader = new(pipe, leaveOpen: true);
      await writer.WriteLineAsync(JsonSerializer.Serialize(request, PrintApiClient.JsonOptions));
      return await reader.ReadLineAsync(token) ?? throw new IOException("Pipe closed before response.");
   }



   /// <summary>Counts cancel calls so the fake server can stop listing the job once it has been cancelled.</summary>
   private sealed class ServerState
   {
      public int Cancellations;
   }



   /// <summary>Fake server: lists one queued job until it is cancelled, has nothing to claim, and answers control polls with 404.</summary>
   private sealed class Handler(ServerState server) : HttpMessageHandler
   {
      /// <summary>Routes by URL suffix: queued list, cancel, claim and control poll; anything else gets an empty JSON object.</summary>
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         string path = request.RequestUri!.AbsolutePath;
         if(path.EndsWith("/control/poll", StringComparison.Ordinal))
         {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
         }
         string body = "{}";
         if(path.EndsWith("/queued", StringComparison.Ordinal))
         {
            PrintJob[] jobs = Volatile.Read(ref server.Cancellations) == 0
               ? [new PrintJob("queued-job", "printer", "Synthetic queued document", "PDF", "unused", 1, new PrintOptions(), DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture), "QUEUED")]
               : [];
            body = JsonSerializer.Serialize(new { jobs }, PrintApiClient.JsonOptions);
         }
         else if(path.EndsWith("/queued-job/cancel", StringComparison.Ordinal))
         {
            Interlocked.Increment(ref server.Cancellations);
            body = "{\"accepted\":true}";
         }
         else if(path.EndsWith("/claim", StringComparison.Ordinal))
         {
            body = "{\"job\":null}";
         }
         return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
      }
   }



   /// <summary>A ready PDF queue that records and rejects any print attempt.</summary>
   private sealed class NoPrintBackend : IPrinterBackend
   {
      public int PrintAttempts { get; private set; }



      /// <summary>Reports the single synthetic queue.</summary>
      public IReadOnlyList<PrinterInfo> Discover() => [new PrinterInfo("printer", "Synthetic printer", "Fixture", "None", "READY", ["PDF"])];



      /// <summary>Counts the attempt and throws, because a queued job must never be printed by these tests.</summary>
      public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
      {
         PrintAttempts++;
         throw new InvalidOperationException("Queued-job tests must never submit a print.");
      }
   }
}
