/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Keep real pipe responses available until current and older clients read them.</summary>
public sealed class AgentRuntimePipeResponseTests
{
   /// <summary>
   /// A client that waits before reading must still get the complete reply, for both the length-framed reader and the older
   /// line reader, whether the operation succeeds or is unknown. Afterwards the single pipe instance must accept the next client,
   /// so one slow reader cannot lock operators out of the service.
   /// </summary>
   [Theory]
   [InlineData("query-jobs", false)]
   [InlineData("query-jobs", true)]
   [InlineData("unknown-synthetic-operation", false)]
   [InlineData("unknown-synthetic-operation", true)]
   public async Task DelayedReader_ReceivesCompleteResponse_AndPipeAcceptsNextClient(string operation, bool legacyReader)
   {
      await using Fixture fixture = new();
      string response = await SendAsync(fixture, operation, legacyReader, delayedReader: true);
      if(string.Equals(operation, "query-jobs", StringComparison.Ordinal))
      {
         JobPage? page = JsonSerializer.Deserialize<JobPage>(response, PrintApiClient.JsonOptions);
         Assert.NotNull(page);
         Assert.Equal(0, page.TotalCount);
         Assert.Empty(page.Jobs);
      }
      else
      {
         Assert.Equal("Error: Unknown command.", response);
      }
      Assert.False(fixture.Running.IsCompleted);
      string next = await SendAsync(fixture, "query-jobs", legacyReader: false, delayedReader: false);
      Assert.Equal(0, JsonSerializer.Deserialize<JobPage>(next, PrintApiClient.JsonOptions)!.TotalCount);
   }



   /// <summary>
   /// If a client sends a request and never reads the reply, stopping the runtime must cancel the pending response wait instead
   /// of hanging service shutdown on that client.
   /// </summary>
   [Fact]
   public async Task Shutdown_CancelsResponseWait_WhenClientNeverReads()
   {
      await using Fixture fixture = new();
      await using NamedPipeClientStream client = new(".", fixture.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
      await client.ConnectAsync(fixture.Stop.Token);
      await AgentPipeFraming.WriteAsync(client, JsonSerializer.Serialize(new PipeRequest("query-jobs"), PrintApiClient.JsonOptions), AgentPipeFraming.MaximumCommandCharacters, fixture.Stop.Token);
      await WaitForResponseOrDisconnectAsync(client, fixture.Stop.Token);
      await fixture.Stop.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Running.WaitAsync(TimeSpan.FromSeconds(3)));
   }



   /// <summary>
   /// The response write on a bare pipe pair keeps waiting for the client to close, but gives up when its own deadline token
   /// fires. This is the bound that stops an unresponsive client from holding the pipe past the service's per-command deadline.
   /// </summary>
   [Fact]
   public async Task ResponseWait_RespectsDeadline_WhenClientNeverReads()
   {
      string name = "QqqPrintAgent-test-response-deadline-" + Guid.NewGuid().ToString("N");
      using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
      await using NamedPipeServerStream server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
         PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
      await using NamedPipeClientStream client = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
      Task connect = client.ConnectAsync(deadline.Token);
      await server.WaitForConnectionAsync(deadline.Token);
      await connect;
      using CancellationTokenSource responseDeadline = new();
      Task response = AgentPipeFraming.WriteResponseAsync(server, "Synthetic response", responseDeadline.Token);
      await WaitForResponseOrDisconnectAsync(client, deadline.Token);
      Assert.False(response.IsCompleted);
      responseDeadline.CancelAfter(TimeSpan.FromMilliseconds(100));
      await response.WaitAsync(TimeSpan.FromSeconds(2), deadline.Token);
      Assert.True(responseDeadline.IsCancellationRequested);
   }



   /// <summary>Observe the buffered response without consuming it, then deliberately delay the reader.</summary>
   private static async Task<string> SendAsync(Fixture fixture, string operation, bool legacyReader, bool delayedReader)
   {
      await using NamedPipeClientStream client = new(".", fixture.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
      await client.ConnectAsync(fixture.Stop.Token);
      string command = JsonSerializer.Serialize(new PipeRequest(operation), PrintApiClient.JsonOptions);
      if(legacyReader)
      {
         using StreamWriter writer = new(client, leaveOpen: true) { AutoFlush = true };
         await writer.WriteLineAsync(command.AsMemory(), fixture.Stop.Token);
      }
      else
      {
         await AgentPipeFraming.WriteAsync(client, command, AgentPipeFraming.MaximumCommandCharacters, fixture.Stop.Token);
      }
      if(delayedReader)
      {
         await WaitForResponseOrDisconnectAsync(client, fixture.Stop.Token);
         await Task.Delay(250, fixture.Stop.Token);
      }
      if(legacyReader)
      {
         using StreamReader reader = new(client, leaveOpen: true);
         return await reader.ReadLineAsync(fixture.Stop.Token) ?? throw new IOException("Pipe closed before the complete response.");
      }
      return await AgentPipeFraming.ReadAsync(client, AgentPipeFraming.MaximumResponseCharacters, fixture.Stop.Token);
   }



   /// <summary>A pre-fix disconnect is also ready to inspect; an arbitrary handler delay is not.</summary>
   private static async Task WaitForResponseOrDisconnectAsync(NamedPipeClientStream client, CancellationToken token)
   {
      while(true)
      {
         if(!PeekNamedPipe(client.SafePipeHandle, IntPtr.Zero, 0, IntPtr.Zero, out uint available, IntPtr.Zero))
         {
            int error = Marshal.GetLastWin32Error();
            if(error is 109 or 233)
            {
               return; // Broken or disconnected pipe.
            }
            throw new Win32Exception(error);
         }
         if(available > 0)
         {
            return;
         }
         await Task.Delay(10, token);
      }
   }



   /// <summary>No configuration, network transport or real printer is present in this runtime fixture.</summary>
   private sealed class Fixture : IAsyncDisposable
   {
      private readonly string _directory = Path.Combine(Path.GetTempPath(), "qqq-pipe-response-" + Guid.NewGuid().ToString("N"));
      private readonly AgentRuntime _runtime;
      public string PipeName { get; } = "QqqPrintAgent-test-response-" + Guid.NewGuid().ToString("N");
      public CancellationTokenSource Stop { get; } = new(TimeSpan.FromSeconds(15));
      public Task Running { get; }



      /// <summary>Starts the runtime on a uniquely named private pipe with small buffers, so tests never touch the installed service's pipe.</summary>
      public Fixture()
      {
         _runtime = new(_directory, _ => { }, new NoPrinters(), pipeName: PipeName,
            pipeFactory: () => new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
               PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096));
         Running = _runtime.RunAsync(Stop.Token);
      }



      /// <summary>Stops the runtime, waits briefly for it to unwind, and removes the temporary state directory.</summary>
      public async ValueTask DisposeAsync()
      {
         await Stop.CancelAsync();
         try
         {
            await Running.WaitAsync(TimeSpan.FromSeconds(3));
         }
         catch(OperationCanceledException) { }
         _runtime.Dispose();
         Stop.Dispose();
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         Directory.Delete(_directory, recursive: true);
      }
   }



   /// <summary>Win32 peek: reports how many bytes are waiting on the pipe without consuming them, so tests can tell a buffered reply from a dropped connection.</summary>
   [DllImport("kernel32.dll", SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool PeekNamedPipe(SafePipeHandle pipe, IntPtr buffer, uint bufferSize, IntPtr bytesRead, out uint totalBytesAvailable, IntPtr bytesLeftThisMessage);



   /// <summary>Printer backend with no queues whose print call throws, so any accidental print fails the test.</summary>
   private sealed class NoPrinters : IPrinterBackend
   {
      /// <summary>Reports an empty printer inventory; the pipe tests only exchange management messages and need no queue.</summary>
      public IReadOnlyList<PrinterInfo> Discover() => [];



      /// <summary>Always throws: physical printing is forbidden here.</summary>
      public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken) => throw new InvalidOperationException("Physical printing is forbidden in this test.");
   }
}
