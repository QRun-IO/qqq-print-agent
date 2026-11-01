/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Net;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Checks the print loop's pacing against a synthetic server: queued work is never delayed by the poll interval.</summary>
public sealed class AgentRuntimeBacklogTests
{
   /// <summary>
   /// With a 20-second poll interval configured, three queued jobs and the empty fourth claim must all complete within seconds, so
   /// a backlog is drained back to back rather than one job per interval. After the queue empties (or the server fails) the loop
   /// must stop claiming and wait, so an idle or failing server is not hammered. Claims are held until the loop is ready so setup
   /// time does not count against the drain budget.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task Backlog_DrainsWithoutPollDelay_ThenBacksOffOnEmptyOrError(bool failWhenEmpty)
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-backlog-" + Guid.NewGuid().ToString("N"));
      string name = "qqq-backlog-" + Guid.NewGuid().ToString("N");
      using CancellationTokenSource stop = new();
      System.Collections.Concurrent.ConcurrentQueue<string> logs = new();
      FakePrinter printer = new();
      ServerState state = new(failWhenEmpty);
      using(AgentStore store = new(directory))
      {
         store.SaveConfiguration(new AgentConfiguration("https://synthetic.invalid", "device", "synthetic", 20));
         store.SetExposure("queue", true, printer.Discover());
      }
      using AgentRuntime runtime = new(directory, logs.Enqueue, printer,
         config => new PrintApiClient(config.ServerUrl, config.DeviceToken, new Handler(state)), name,
         () => new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly));
      Task running = runtime.RunAsync(stop.Token);
      try
      {
         // Synthetic responses otherwise let RunAsync drain synchronously before this
         // assertion starts. Exclude setup/control readiness from the drain budget.
         await state.FirstClaim.Task.WaitAsync(TimeSpan.FromSeconds(10));
         stop.CancelAfter(TimeSpan.FromSeconds(15));
         state.ReleaseClaims.SetResult();
         // Four claims must finish before the configured 20-second idle interval.
         await state.Empty.Task.WaitAsync(TimeSpan.FromSeconds(10), stop.Token);
         Assert.Equal(3, printer.Printed);
         await Task.Delay(250, stop.Token);
         Assert.Equal(4, Volatile.Read(ref state.Claims));
         Assert.False(running.IsCompleted);
      }
      catch(Exception exception) when(exception is TimeoutException or OperationCanceledException)
      {
         throw new TimeoutException($"Backlog fixture timed out: claims={Volatile.Read(ref state.Claims)}, printed={printer.Printed}, firstClaim={state.FirstClaim.Task.IsCompleted}, runtimeCompleted={running.IsCompleted}; logs={string.Join(" | ", logs.TakeLast(8))}", exception);
      }
      finally
      {
         await stop.CancelAsync();
         try
         {
            await running;
         }
         catch(OperationCanceledException) { }
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>Shared counters and gates between the test and the fake server: when the first claim arrived, when claims may proceed, and when the queue ran dry.</summary>
   private sealed class ServerState(bool failWhenEmpty)
   {
      public int Claims;
      public bool FailWhenEmpty { get; } = failWhenEmpty;
      public TaskCompletionSource FirstClaim { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      public TaskCompletionSource ReleaseClaims { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      public TaskCompletionSource Empty { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
   }



   /// <summary>Fake print server: no control channel, one-byte documents, three jobs on the first three claims, then an empty (or failing) queue.</summary>
   private sealed class Handler(ServerState state) : HttpMessageHandler
   {
      /// <summary>Answers control polls with 404, content with one byte and claims from the scripted queue, signalling the test at the first and the empty claim.</summary>
      protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         string path = request.RequestUri!.AbsolutePath;
         if(path.EndsWith("/control/poll", StringComparison.Ordinal))
         {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
         }
         if(path.EndsWith("/content", StringComparison.Ordinal))
         {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
         }
         PrintJob? job = null;
         if(path.EndsWith("/claim", StringComparison.Ordinal))
         {
            state.FirstClaim.TrySetResult();
            await state.ReleaseClaims.Task.WaitAsync(cancellationToken);
            int claim = Interlocked.Increment(ref state.Claims);
            if(claim <= 3)
            {
               job = new PrintJob("job" + claim.ToString(System.Globalization.CultureInfo.InvariantCulture), "queue", "Synthetic", "PDF",
                  Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1])), 1, new PrintOptions(), "", "CLAIMED");
            }
            else
            {
               state.Empty.TrySetResult();
               if(state.FailWhenEmpty)
               {
                  return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
               }
            }
         }
         return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { job }, PrintApiClient.JsonOptions)) };
      }
   }



   /// <summary>One ready PDF queue that counts documents instead of printing; no physical device is involved.</summary>
   private sealed class FakePrinter : IPrinterBackend
   {
      public int Printed { get; private set; }



      /// <summary>Reports the single synthetic queue.</summary>
      public IReadOnlyList<PrinterInfo> Discover() => [new("queue", "Synthetic", "Fake", "Fake", "READY", ["PDF"])];



      /// <summary>Counts the document and returns a fixed receipt.</summary>
      public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
      {
         Printed++;
         return Task.FromResult("fake-receipt");
      }
   }
}
