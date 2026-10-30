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

public sealed class AgentRuntimeUpdateTests
{
   [Fact]
   public async Task UpdatePreparationDrainsActiveWorkThenPausesClaimsButKeepsHeartbeats()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-update-" + Guid.NewGuid().ToString("N"));
      string pipeName = "QqqUpdate-test-" + Guid.NewGuid().ToString("N");
      using CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
      ServerState server = new();
      using(AgentStore store = new(directory))
      {
         store.SaveConfiguration(new AgentConfiguration("https://example.invalid", "device", "synthetic-token", 1));
      }
      using AgentRuntime runtime = new(directory, _ => { }, new NoPrintBackend(),
         configuration => new PrintApiClient(configuration.ServerUrl, configuration.DeviceToken, new Handler(server)),
         pipeName, () => new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
      { HeartbeatInterval = TimeSpan.FromMilliseconds(50) };
      Task running = runtime.RunAsync(stop.Token);
      try
      {
         await server.Entered.Task.WaitAsync(stop.Token);
         Task<string> preparation = SendAsync(pipeName, "prepare-update", stop.Token);
         await Task.Delay(100, stop.Token);
         Assert.False(preparation.IsCompleted);
         server.Release.TrySetResult();
         Assert.Equal("Ready for update.", await preparation);
         int claims = Volatile.Read(ref server.Claims);
         int heartbeats = Volatile.Read(ref server.Heartbeats);
         await Task.Delay(1200, stop.Token);
         Assert.Equal(claims, Volatile.Read(ref server.Claims));
         Assert.True(Volatile.Read(ref server.Heartbeats) > heartbeats);
         Assert.Equal("Printing resumed.", await SendAsync(pipeName, "resume-after-update", stop.Token));
         while(Volatile.Read(ref server.Claims) == claims)
         {
            await Task.Delay(10, stop.Token);
         }
      }
      finally
      {
         await stop.CancelAsync();
         try
         { await running; }
         catch(OperationCanceledException) { }
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
      }
   }

   private static async Task<string> SendAsync(string name, string operation, CancellationToken token)
   {
      await using NamedPipeClientStream pipe = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
      await pipe.ConnectAsync(token);
      using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };
      using StreamReader reader = new(pipe, leaveOpen: true);
      await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeRequest(operation), PrintApiClient.JsonOptions));
      return await reader.ReadLineAsync(token) ?? throw new IOException("Pipe closed.");
   }

   private sealed class ServerState
   {
      public int Claims;
      public int Heartbeats;
      public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
   }

   private sealed class Handler(ServerState state) : HttpMessageHandler
   {
      protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         if(request.RequestUri!.AbsolutePath.EndsWith("/claim", StringComparison.Ordinal))
         {
            Interlocked.Increment(ref state.Claims);
            state.Entered.TrySetResult();
            await state.Release.Task.WaitAsync(cancellationToken);
         }
         if(request.RequestUri!.AbsolutePath.EndsWith("/heartbeat", StringComparison.Ordinal))
         {
            Interlocked.Increment(ref state.Heartbeats);
         }
         return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
      }
   }

   private sealed class NoPrintBackend : IPrinterBackend
   {
      public IReadOnlyList<PrinterInfo> Discover() => [];
      public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
         => throw new InvalidOperationException("Update tests must never print.");
   }
}
