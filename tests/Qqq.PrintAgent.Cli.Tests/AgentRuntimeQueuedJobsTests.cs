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

public sealed class AgentRuntimeQueuedJobsTests
{
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

   private static async Task VerifyQueuedDetailsAsync(string pipeName, string jobId, CancellationToken token)
   {
      string text = await SendAsync(pipeName, new PipeRequest("job-details", JobId: jobId), token);
      JobDetailsSnapshot details = JsonSerializer.Deserialize<JobDetailsSnapshot>(text, PrintApiClient.JsonOptions)!;
      Assert.Equal("QUEUED", details.Job.Status);
      Assert.Empty(details.Events);
   }

   private static async Task VerifyPagedPrinterNameAsync(string pipeName, CancellationToken token)
   {
      string text = await SendAsync(pipeName, new PipeRequest("query-jobs", JobQuery: new JobQuery(SortBy: "printerName", Descending: false)), token);
      JobPage page = JsonSerializer.Deserialize<JobPage>(text, PrintApiClient.JsonOptions)!;
      Assert.Equal("Dispatch labels", Assert.Single(page.Jobs).PrinterName);
   }

   private static async Task<string> SendAsync(string name, PipeRequest request, CancellationToken token)
   {
      await using NamedPipeClientStream pipe = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
      await pipe.ConnectAsync(token);
      using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };
      using StreamReader reader = new(pipe, leaveOpen: true);
      await writer.WriteLineAsync(JsonSerializer.Serialize(request, PrintApiClient.JsonOptions));
      return await reader.ReadLineAsync(token) ?? throw new IOException("Pipe closed before response.");
   }

   private sealed class ServerState
   {
      public int Cancellations;
   }

   private sealed class Handler(ServerState server) : HttpMessageHandler
   {
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         string path = request.RequestUri!.AbsolutePath;
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

   private sealed class NoPrintBackend : IPrinterBackend
   {
      public int PrintAttempts { get; private set; }
      public IReadOnlyList<PrinterInfo> Discover() => [new PrinterInfo("printer", "Synthetic printer", "Fixture", "None", "READY", ["PDF"])];
      public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
      {
         PrintAttempts++;
         throw new InvalidOperationException("Queued-job tests must never submit a print.");
      }
   }
}
