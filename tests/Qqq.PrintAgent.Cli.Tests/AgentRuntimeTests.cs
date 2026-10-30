using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentRuntimeTests
{
   [Fact]
   public async Task Reconnect_WakesPollingAfterHttpTimeout_AndRetainsPipeAfterClientDisconnect()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-runtime-" + Guid.NewGuid().ToString("N"));
      string pipeName = "QqqPrintAgent-test-" + Guid.NewGuid().ToString("N");
      using CancellationTokenSource stop = new(TimeSpan.FromSeconds(20));
      TaskCompletionSource failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
      TaskCompletionSource recovered = new(TaskCreationOptions.RunContinuationsAsynchronously);
      int requests = 0;
      Task? running = null;
      AgentRuntime? runtime = null;
      try
      {
         using(AgentStore store = new(directory))
         { store.SaveConfiguration(new AgentConfiguration("https://example.com", "test-device", "test-token")); }
         runtime = new(directory, message =>
         {
            if(message.Contains("simulated timeout", StringComparison.Ordinal))
            { failed.TrySetResult(); }
            if(string.Equals(message, "Connected to the print server.", StringComparison.Ordinal))
            { recovered.TrySetResult(); }
         }, new NoPrinters(), configuration => new PrintApiClient(configuration.ServerUrl, configuration.DeviceToken, new Handler(() => Interlocked.Increment(ref requests))), pipeName, () => new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly));
         running = runtime.RunAsync(stop.Token);
         await failed.Task.WaitAsync(stop.Token);
         string result = await SendAsync(pipeName, new PipeRequest("reconnect"), stop.Token);
         Assert.StartsWith("Reconnect requested", result, StringComparison.Ordinal);
         await recovered.Task.WaitAsync(TimeSpan.FromSeconds(3), stop.Token);
         Assert.True(runtime.GetSnapshot().Connected);
         await VerifyJobActionsAsync(directory, pipeName, runtime, stop.Token);
         await DisconnectMidCommandAsync(pipeName, stop.Token);
         string status = await SendAsync(pipeName, new PipeRequest("status"), stop.Token);
         Assert.Contains("Connected", status, StringComparison.Ordinal);
         Assert.False(running.IsCompleted);
         await stop.CancelAsync();
         await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
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

   private static async Task VerifyJobActionsAsync(string directory, string pipeName, AgentRuntime runtime, CancellationToken token)
   {
      string denied = await SendAsync(pipeName, new PipeRequest("test-print", PrinterId: "test-printer", Format: "PDF"), token);
      Assert.StartsWith("Error: Share the printer", denied, StringComparison.Ordinal);
      using(AgentStore store = new(directory))
      {
         store.SetExposure("test-printer", true, new NoPrinters().Discover());
         store.Record("failed-job", new PrintEvent("test-failed", "FAILED", "Synthetic failure", null));
      }
      Assert.Equal("RED", runtime.GetSnapshot().Health);
      string details = await SendAsync(pipeName, new PipeRequest("job-details", JobId: "failed-job"), token);
      Assert.Contains("Synthetic failure", details, StringComparison.Ordinal);
      string acknowledged = await SendAsync(pipeName, new PipeRequest("acknowledge-job", JobId: "failed-job"), token);
      Assert.StartsWith("Job acknowledged", acknowledged, StringComparison.Ordinal);
      Assert.Equal("GREEN", runtime.GetSnapshot().Health);
      string queued = await SendAsync(pipeName, new PipeRequest("test-print", PrinterId: "test-printer", Format: "PDF"), token);
      Assert.StartsWith("Queued on the server", queued, StringComparison.Ordinal);
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

   private static async Task DisconnectMidCommandAsync(string name, CancellationToken token)
   {
      await using NamedPipeClientStream pipe = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
      await pipe.ConnectAsync(token);
      await pipe.WriteAsync(Encoding.UTF8.GetBytes("{invalid"), token);
      await pipe.FlushAsync(token);
   }

   private sealed class Handler(Func<int> count) : HttpMessageHandler
   {
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         if(count() == 1)
         { throw new TaskCanceledException("simulated timeout"); }
         return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
         {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("test-print", StringComparison.Ordinal) ? "{\"jobId\":\"test-queued\"}" : "{}", Encoding.UTF8, "application/json"),
         });
      }
   }

   private sealed class NoPrinters : IPrinterBackend
   {
      public IReadOnlyList<PrinterInfo> Discover() => [new PrinterInfo("test-printer", "Test printer", "Test driver", "Test port", "READY", ["PDF"])];
      public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken) => throw new InvalidOperationException("No physical printing is allowed in this test.");
   }
}
