using System.IO.Pipes;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentRealtimeRecoveryTests
{
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
