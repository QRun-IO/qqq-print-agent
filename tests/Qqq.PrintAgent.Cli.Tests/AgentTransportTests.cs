/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentTransportTests
{
   [Fact]
   public async Task DispatchDisconnectKeepsWebSocketConnectedAndReconnectPreservesTrafficTotals()
   {
      await using var fixture = new RealtimeRuntimeFixture();
      RealtimeRuntimeFixture.Connection connection = await fixture.ConnectAsync();
      AgentTransportSnapshot before = fixture.Runtime.GetTransportSnapshot();
      Assert.Equal("WS", before.Protocol);
      Assert.Equal("CONNECTED", before.State);
      Assert.True(before.UploadedBytes > 0);
      Assert.True(before.DownloadedBytes > 0);
      await connection.SendAsync("DESIRED_STATE", new DeviceDesiredState(2, false, "DISCONNECTED"));
      await fixture.WaitAsync(() => fixture.Runtime.GetSnapshot().Control!.DesiredState.ConnectionState is "DISCONNECTED"
         && fixture.Runtime.GetTransportSnapshot().State is "CONNECTED");
      Assert.False(fixture.Runtime.GetSnapshot().Connected);
      Assert.Equal("CONNECTED", fixture.Runtime.GetSnapshot().Transport!.State);
      connection.Abort();
      await fixture.WaitAsync(() => fixture.Runtime.GetTransportSnapshot().State is "RECONNECTING");
      _ = await fixture.ConnectAsync(desiredState: new DeviceDesiredState(3, false, "CONNECTED"));
      AgentTransportSnapshot after = fixture.Runtime.GetTransportSnapshot();
      Assert.Equal("CONNECTED", after.State);
      Assert.True(after.UploadedBytes > before.UploadedBytes);
      Assert.True(after.DownloadedBytes > before.DownloadedBytes);
      Assert.Equal(before.TotalsSinceUtc, after.TotalsSinceUtc);
      Assert.Equal(0, fixture.ForbiddenPolls);
      Assert.Equal(0, fixture.Printer.Prints);
   }

   [Fact]
   public async Task TransportPipeOperationDoesNotDiscoverPrintersAndReturnsOnlyTrafficStatus()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-transport-pipe-" + Guid.NewGuid().ToString("N"));
      string name = "qqq-transport-" + Guid.NewGuid().ToString("N");
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
      var runtime = new AgentRuntime(directory, _ => { }, new ForbiddenPrinters(), pipeName: name,
         pipeFactory: () => new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly));
      Task run = runtime.RunAsync(timeout.Token);
      try
      {
         using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
         await client.ConnectAsync(timeout.Token);
         await AgentPipeFraming.WriteAsync(client, """{"operation":"transport"}""", AgentPipeFraming.MaximumCommandCharacters, timeout.Token);
         string json = await AgentPipeFraming.ReadAsync(client, AgentPipeFraming.MaximumResponseCharacters, timeout.Token);
         AgentTransportSnapshot status = JsonSerializer.Deserialize<AgentTransportSnapshot>(json, PrintApiClient.JsonOptions)!;
         Assert.Equal("NONE", status.Protocol);
         Assert.Equal("DISCONNECTED", status.State);
         Assert.Equal(0, status.UploadedBytes);
         Assert.Equal(0, status.DownloadedBytes);
         Assert.DoesNotContain("printers", json, StringComparison.OrdinalIgnoreCase);
         Assert.DoesNotContain("jobs", json, StringComparison.OrdinalIgnoreCase);
      }
      finally
      {
         await timeout.CancelAsync();
         try
         { await run; }
         catch(OperationCanceledException) { }
         runtime.Dispose();
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
      }
   }

   [Fact]
   public void OldSnapshotHasUnavailableTelemetryRatherThanFabricatedZero()
   {
      const string json = """{"enrolled":true,"connected":true,"health":"ONLINE","status":"Connected","serverUrl":"https://synthetic.invalid","deviceId":"synthetic","lastSuccessfulPoll":null,"printers":[],"jobs":[],"logs":[]}""";
      AgentSnapshot snapshot = JsonSerializer.Deserialize<AgentSnapshot>(json, PrintApiClient.JsonOptions)!;
      Assert.Null(snapshot.Transport);
   }

   private sealed class ForbiddenPrinters : IPrinterBackend
   {
      public IReadOnlyList<PrinterInfo> Discover() => throw new InvalidOperationException("Transport observation must not discover printers.");
      public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
         => throw new InvalidOperationException("Physical printing is forbidden in this test.");
   }
}
