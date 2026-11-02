/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>The transport status (protocol, connection state and traffic totals) shown in the footer must be truthful: separate from the dispatch on/off setting, durable across reconnects, cheap to read, and honest when unavailable.</summary>
public sealed class AgentTransportTests
{
   /// <summary>
   /// Turning dispatch off at the server does not close the WebSocket: the channel stays CONNECTED even though the agent reports
   /// itself disconnected. After an actual network drop the state shows RECONNECTING, and once reconnected the byte totals have
   /// grown and still count from the original start time, so reconnects never reset the counters or cause HTTP polling or printing.
   /// </summary>
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



   /// <summary>The pipe's transport request returns only the connection and traffic figures: an idle agent reports NONE/DISCONNECTED with zero bytes, no printer or job data appears in the reply, and the printer backend is never asked to discover queues (the fake throws if it is).</summary>
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



   /// <summary>A snapshot written by an older service has no transport section; it reads back as null (unavailable) instead of an invented zero, so the screen can say the figure is unknown.</summary>
   [Fact]
   public void OldSnapshotHasUnavailableTelemetryRatherThanFabricatedZero()
   {
      const string json = """{"enrolled":true,"connected":true,"health":"ONLINE","status":"Connected","serverUrl":"https://synthetic.invalid","deviceId":"synthetic","lastSuccessfulPoll":null,"printers":[],"jobs":[],"logs":[]}""";
      AgentSnapshot snapshot = JsonSerializer.Deserialize<AgentSnapshot>(json, PrintApiClient.JsonOptions)!;
      Assert.Null(snapshot.Transport);
   }



   /// <summary>A printer backend that throws on any use, so a test fails if transport observation touches printers or prints.</summary>
   private sealed class ForbiddenPrinters : IPrinterBackend
   {
      /// <summary>Always throws: transport observation must not discover printers.</summary>
      public IReadOnlyList<PrinterInfo> Discover() => throw new InvalidOperationException("Transport observation must not discover printers.");



      /// <summary>Always throws: physical printing is forbidden here.</summary>
      public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
         => throw new InvalidOperationException("Physical printing is forbidden in this test.");
   }
}
