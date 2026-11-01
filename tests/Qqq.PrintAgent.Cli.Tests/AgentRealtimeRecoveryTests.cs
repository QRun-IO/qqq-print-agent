/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Starting up when the saved device proof is damaged: the service must still come up and answer on its management pipe so the operator can repair it, while the damaged file and the job history stay untouched.</summary>
public sealed class AgentRealtimeRecoveryTests
{
   /// <summary>With an unreadable device proof file the runtime keeps running and serves a snapshot reporting "not enrolled, not connected", leaves the damaged bytes as they are, and after the original file is restored the job journal and shared printers are intact, so a recoverable fault never costs the history.</summary>
   [Fact]
   public async Task UnreadableOldProofStillStartsManagementPipeAndPreservesOriginalJournal()
   {
      using var fixture = new ControlFixture();
      fixture.SeedSpooledJob();
      string path = Path.Combine(fixture.DirectoryPath, "connection.dpapi");
      byte[] original = File.ReadAllBytes(path);
      byte[] unreadable = [1, 3, 5];
      File.WriteAllBytes(path, unreadable);
      string name = "qqq-recovery-" + Guid.NewGuid().ToString("N");
      using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
      using var runtime = new AgentRuntime(fixture.DirectoryPath, _ => { }, fixture.Printer, pipeName: name,
         pipeFactory: () => new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly));
      Task running = runtime.RunAsync(stop.Token);
      try
      {
         await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
         await pipe.ConnectAsync(stop.Token);
         await AgentPipeFraming.WriteAsync(pipe, JsonSerializer.Serialize(new PipeRequest("snapshot"), PrintApiClient.JsonOptions), AgentPipeFraming.MaximumCommandCharacters, stop.Token);
         string response = await AgentPipeFraming.ReadAsync(pipe, AgentPipeFraming.MaximumResponseCharacters, stop.Token);
         AgentSnapshot snapshot = JsonSerializer.Deserialize<AgentSnapshot>(response, PrintApiClient.JsonOptions)!;
         Assert.False(snapshot.Enrolled);
         Assert.False(snapshot.Connected);
         Assert.False(running.IsCompleted);
         Assert.Equal(unreadable, File.ReadAllBytes(path));
      }
      finally
      {
         await stop.CancelAsync();
         await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
         File.WriteAllBytes(path, original);
      }
      using var store = new AgentStore(fixture.DirectoryPath);
      Assert.Equal("SPOOLED", store.GetState("job"));
      Assert.Contains("printer", store.GetExposedPrinterIds());
   }
}
