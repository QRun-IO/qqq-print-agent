/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Operator-chosen device and printer descriptions must survive every hop between the companion and the server.</summary>
public sealed class AgentRuntimeProfileTests
{
   /// <summary>
   /// Full-length non-Latin profiles (512 and 4,096 characters) are sent over the management pipe, which escapes them into a
   /// request larger than 8 KiB but within the 64 KiB command limit. They must come back in the snapshot, persist in the journal,
   /// and reach the server unchanged in both the heartbeat and the printer inventory, while the Windows queue name and driver
   /// stay the real ones rather than being replaced by the display name.
   /// </summary>
   [Fact]
   public async Task FullUnicodeProfiles_RoundTripThroughPipePersistenceHeartbeatAndInventory()
   {
      await using Fixture fixture = new();
      DeviceProfile device = new(new string('漢', 512), new string('語', 4096), new string('場', 512));
      PrinterProfile printer = new("Shipping labels", new string('印', 4096), "Dock 2");
      PipeRequest deviceRequest = new("set-device-profile", DeviceProfile: device);
      string encoded = JsonSerializer.Serialize(deviceRequest, PrintApiClient.JsonOptions);
      Assert.Contains("\\u", encoded, StringComparison.Ordinal);
      Assert.InRange(encoded.Length, 8193, 65536);
      AgentSnapshot deviceSnapshot = await fixture.SendAsync(deviceRequest);
      Assert.Equal(device, deviceSnapshot.DeviceProfile);
      AgentSnapshot printerSnapshot = await fixture.SendAsync(new PipeRequest("set-printer-profile", PrinterId: "queue-id", PrinterProfile: printer));
      PrinterSnapshot visible = Assert.Single(printerSnapshot.Printers);
      Assert.Equal("queue-id", visible.PrinterId);
      Assert.Equal("Windows Queue", visible.Name);
      Assert.Equal(printer.DisplayName, visible.DisplayName);
      Assert.Equal(printer.Description, visible.Description);
      Assert.Equal(printer.Location, visible.Location);
      DeviceProfile heartbeat = await fixture.DeviceSeen.Task.WaitAsync(fixture.Token);
      PrinterInfo inventory = await fixture.PrinterSeen.Task.WaitAsync(fixture.Token);
      Assert.Equal(device, heartbeat);
      Assert.Equal("queue-id", inventory.PrinterId);
      Assert.Equal("Windows Queue", inventory.Name);
      Assert.Equal("Synthetic driver", inventory.DriverName);
      Assert.Equal("TEST", inventory.PortName);
      Assert.Equal(printer.DisplayName, inventory.DisplayName);
      Assert.Equal(printer.Description, inventory.Description);
      Assert.Equal(printer.Location, inventory.Location);
      using AgentStore persisted = new(fixture.Directory);
      Assert.Equal(device, persisted.GetDeviceProfile());
      Assert.Equal(printer, persisted.GetPrinterProfile("queue-id"));
      Assert.Equal("device", persisted.LoadConfiguration()!.DeviceId);
      Assert.Contains("queue-id", persisted.GetExposedPrinterIds());
   }



   /// <summary>A running agent runtime on a private test pipe with a fast heartbeat, a fake server and a no-print backend, plus signals for what the server saw.</summary>
   private sealed class Fixture : IAsyncDisposable
   {
      private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
      private readonly string _pipeName = "QqqProfiles-test-" + Guid.NewGuid().ToString("N");
      private readonly AgentRuntime _runtime;
      private readonly Task _running;
      public string Directory { get; } = Path.Combine(Path.GetTempPath(), "qqq-profile-runtime-" + Guid.NewGuid().ToString("N"));
      public CancellationToken Token => _stop.Token;
      public TaskCompletionSource<DeviceProfile> DeviceSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      public TaskCompletionSource<PrinterInfo> PrinterSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);



      /// <summary>Seeds an enrolled configuration with the queue already shared, then starts the runtime heartbeating every 50 ms.</summary>
      public Fixture()
      {
         NoPrintBackend backend = new();
         using(AgentStore store = new(Directory))
         {
            store.SaveConfiguration(new AgentConfiguration("https://example.invalid", "device", "synthetic-token", 1));
            store.SetExposure("queue-id", true, backend.Discover());
         }
         _runtime = new AgentRuntime(Directory, _ => { }, backend,
            configuration => new PrintApiClient(configuration.ServerUrl, configuration.DeviceToken, new Handler(this)),
            _pipeName, () => new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
         { HeartbeatInterval = TimeSpan.FromMilliseconds(50) };
         _running = _runtime.RunAsync(Token);
      }



      /// <summary>Sends one request over the pipe and returns the snapshot the service replies with.</summary>
      public async Task<AgentSnapshot> SendAsync(PipeRequest request)
      {
         await using NamedPipeClientStream pipe = new(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
         await pipe.ConnectAsync(Token);
         using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };
         using StreamReader reader = new(pipe, leaveOpen: true);
         await writer.WriteLineAsync(JsonSerializer.Serialize(request, PrintApiClient.JsonOptions));
         string result = await reader.ReadLineAsync(Token) ?? throw new IOException("Pipe closed.");
         return JsonSerializer.Deserialize<AgentSnapshot>(result, PrintApiClient.JsonOptions) ?? throw new IOException("Snapshot missing.");
      }



      /// <summary>Stops the runtime, waits for it to unwind, clears SQLite's pools and deletes the temporary state directory.</summary>
      public async ValueTask DisposeAsync()
      {
         await _stop.CancelAsync();
         try
         {
            await _running;
         }
         catch(OperationCanceledException) { }
         _runtime.Dispose();
         _stop.Dispose();
         SqliteConnection.ClearAllPools();
         System.IO.Directory.Delete(Directory, recursive: true);
      }
   }



   /// <summary>Fake server that checks the bearer token and reports the full-length device profile and printer description it receives.</summary>
   private sealed class Handler(Fixture fixture) : HttpMessageHandler
   {
      /// <summary>Control polls get 404; heartbeat and inventory bodies are inspected for the 4,096-character descriptions and signal the fixture.</summary>
      protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         if(request.RequestUri!.AbsolutePath.EndsWith("/control/poll", StringComparison.Ordinal))
         {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
         }
         Assert.Equal("synthetic-token", request.Headers.Authorization?.Parameter);
         string path = request.RequestUri!.AbsolutePath;
         if(request.Content is not null)
         {
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            if(path.EndsWith("/heartbeat", StringComparison.Ordinal) && body.RootElement.TryGetProperty("profile", out JsonElement profile) && profile.ValueKind == JsonValueKind.Object)
            {
               DeviceProfile? device = profile.Deserialize<DeviceProfile>(PrintApiClient.JsonOptions);
               if(device?.Description?.Length == 4096)
               {
                  fixture.DeviceSeen.TrySetResult(device);
               }
            }
            if(path.EndsWith("/printers", StringComparison.Ordinal))
            {
               PrinterInfo[] printers = body.RootElement.GetProperty("printers").Deserialize<PrinterInfo[]>(PrintApiClient.JsonOptions)!;
               if(printers.Length == 1 && printers[0].Description?.Length == 4096)
               {
                  fixture.PrinterSeen.TrySetResult(printers[0]);
               }
            }
         }
         return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
      }
   }



   /// <summary>One ready synthetic queue whose print call throws, because metadata tests must never print.</summary>
   private sealed class NoPrintBackend : IPrinterBackend
   {
      /// <summary>Reports the single synthetic queue.</summary>
      public IReadOnlyList<PrinterInfo> Discover() => [new("queue-id", "Windows Queue", "Synthetic driver", "TEST", "READY", ["PDF"])];



      /// <summary>Always throws, because a metadata test must never send a document to a printer.</summary>
      public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
         => throw new InvalidOperationException("Metadata tests must never print.");
   }
}
