/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;


namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentRuntimeHeartbeatTests
{
   [Fact]
   public async Task HeartbeatsContinueAndUpdateRealContactDuringBlockedSubmission()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-heartbeat-" + Guid.NewGuid().ToString("N"));
      string pipeName = "QqqHeartbeat-test-" + Guid.NewGuid().ToString("N");
      using CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
      BlockingPrinter printer = new();
      int heartbeats = 0;
      Task? running = null;
      AgentRuntime? runtime = null;
      try
      {
         using(AgentStore store = new(directory))
         {
            store.SaveConfiguration(new AgentConfiguration("https://example.invalid", "device", "synthetic-token"));
            store.SetExposure("printer", true, printer.Discover());
         }
         runtime = new AgentRuntime(directory, _ => { }, printer,
            configuration => new PrintApiClient(configuration.ServerUrl, configuration.DeviceToken, new Handler(() => Interlocked.Increment(ref heartbeats))),
            pipeName, () => new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
         {
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
         };
         running = runtime.RunAsync(stop.Token);
         await printer.Entered.Task.WaitAsync(stop.Token);
         int before = Volatile.Read(ref heartbeats);
         while(Volatile.Read(ref heartbeats) < before + 3)
         {
            await Task.Delay(10, stop.Token);
         }
         AgentSnapshot snapshot = runtime.GetSnapshot();
         Assert.True(snapshot.Connected);
         Assert.NotNull(snapshot.LastSuccessfulPoll);
         Assert.Equal("SUBMITTING", Assert.Single(snapshot.Jobs).Status);
         Assert.False(running.IsCompleted);
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

   private sealed class BlockingPrinter : IPrinterBackend
   {
      public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      public IReadOnlyList<PrinterInfo> Discover() => [new PrinterInfo("printer", "Synthetic", "Fixture", "None", "READY", ["PDF"])];
      public async Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
      {
         Entered.TrySetResult();
         await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
         throw new InvalidOperationException("Synthetic submission should end only on cancellation.");
      }
   }

   private sealed class Handler(Action heartbeat) : HttpMessageHandler
   {
      private static readonly byte[] Content = [1, 2, 3];
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         string path = request.RequestUri!.AbsolutePath;
         string body = "{}";
         if(path.EndsWith("heartbeat", StringComparison.Ordinal))
         {
            heartbeat();
         }
         else if(path.EndsWith("claim", StringComparison.Ordinal))
         {
            PrintJob job = new("job", "printer", "Synthetic blocking job", "PDF", Convert.ToHexString(SHA256.HashData(Content)).ToLowerInvariant(), Content.Length, new PrintOptions(), DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture), "CLAIMED");
            body = JsonSerializer.Serialize(new { job }, PrintApiClient.JsonOptions);
         }
         else if(path.EndsWith("content", StringComparison.Ordinal))
         {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) });
         }
         return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
      }
   }
}
