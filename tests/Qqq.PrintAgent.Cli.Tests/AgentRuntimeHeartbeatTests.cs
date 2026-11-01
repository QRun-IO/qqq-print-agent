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

/// <summary>Checks that the agent keeps telling the server it is alive while a print is stuck, so a slow spooler is not mistaken for a dead device.</summary>
public sealed class AgentRuntimeHeartbeatTests
{
   /// <summary>
   /// While a submission blocks indefinitely in the printer, at least three more heartbeats reach the server at the 50 ms test
   /// interval, the snapshot still reports connected with a recent successful poll, and the job shows SUBMITTING. Heartbeats
   /// therefore run independently of the print in progress.
   /// </summary>
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



   /// <summary>A printer whose print call signals the test and then never finishes until cancelled, simulating a spooler submission that hangs.</summary>
   private sealed class BlockingPrinter : IPrinterBackend
   {
      public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);



      /// <summary>Reports the single synthetic queue.</summary>
      public IReadOnlyList<PrinterInfo> Discover() => [new PrinterInfo("printer", "Synthetic", "Fixture", "None", "READY", ["PDF"])];



      /// <summary>Signals that submission started, then waits until cancelled; ending any other way is a test failure.</summary>
      public async Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
      {
         Entered.TrySetResult();
         await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
         throw new InvalidOperationException("Synthetic submission should end only on cancellation.");
      }
   }



   /// <summary>Fake server: counts heartbeats through the supplied callback, offers one verified three-byte job on claim, and serves its content.</summary>
   private sealed class Handler(Action heartbeat) : HttpMessageHandler
   {
      private static readonly byte[] Content = [1, 2, 3];



      /// <summary>Control polls get 404; heartbeat, claim and content requests are answered by URL suffix; everything else returns an empty object.</summary>
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         string path = request.RequestUri!.AbsolutePath;
         if(path.EndsWith("/control/poll", StringComparison.Ordinal))
         {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
         }
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
