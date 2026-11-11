/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Operator-chosen device and printer descriptions must survive every hop between the companion and the server.</summary>
public sealed class AgentRuntimeProfileTests
{
   /// <summary>
   /// Full-length non-Latin profiles (512 and 4,096 characters) are sent over the management pipe, which escapes them into a
   /// request larger than 8 KiB but within the 64 KiB command limit. They must come back in the snapshot, persist in the journal,
   /// and reach the server unchanged: the printer profile in the inventory the change triggers, the device profile in the next
   /// heartbeat. The Windows queue name and driver stay the real ones rather than being replaced by the display name.
   /// </summary>
   [Fact]
   public async Task FullUnicodeProfiles_RoundTripThroughPipePersistenceHeartbeatAndInventory()
   {
      var clock = new ManualTimeProvider();
      await using RealtimeRuntimeFixture fixture = new(timeProvider: clock);
      _ = await fixture.ConnectAsync();
      DeviceProfile device = new(new string('漢', 512), new string('語', 4096), new string('場', 512));
      PrinterProfile printer = new("Shipping labels", new string('印', 4096), "Dock 2");
      PipeRequest deviceRequest = new("set-device-profile", DeviceProfile: device);
      string encoded = JsonSerializer.Serialize(deviceRequest, PrintApiClient.JsonOptions);
      Assert.Contains("\\u", encoded, StringComparison.Ordinal);
      Assert.InRange(encoded.Length, 8193, 65536);
      AgentSnapshot deviceSnapshot = Snapshot(await fixture.PipeAsync(deviceRequest));
      Assert.Equal(device, deviceSnapshot.DeviceProfile);
      AgentSnapshot printerSnapshot = Snapshot(await fixture.PipeAsync(new PipeRequest("set-printer-profile", PrinterId: "printer", PrinterProfile: printer)));
      PrinterSnapshot visible = printerSnapshot.Printers.Single(item => string.Equals(item.PrinterId, "printer", StringComparison.Ordinal));
      Assert.Equal("Synthetic", visible.Name);
      Assert.Equal(printer.DisplayName, visible.DisplayName);
      Assert.Equal(printer.Description, visible.Description);
      Assert.Equal(printer.Location, visible.Location);
      await fixture.WaitAsync(() => DescribedPrinter(fixture) is not null);
      PrinterInfo inventory = DescribedPrinter(fixture)!;
      Assert.Equal("printer", inventory.PrinterId);
      Assert.Equal("Synthetic", inventory.Name);
      Assert.Equal("Fixture", inventory.DriverName);
      Assert.Equal("None", inventory.PortName);
      Assert.Equal(printer.DisplayName, inventory.DisplayName);
      Assert.Equal(printer.Description, inventory.Description);
      Assert.Equal(printer.Location, inventory.Location);
      await fixture.HeartbeatAsync(clock);
      await fixture.WaitAsync(() => DescribedDevice(fixture) is not null);
      Assert.Equal(device, DescribedDevice(fixture));
      using JournalStore persisted = TestState.OpenStore(fixture.DirectoryPath);
      Assert.Equal(device, persisted.GetDeviceProfile());
      Assert.Equal(printer, persisted.GetPrinterProfile("printer"));
      Assert.Equal("device", persisted.LoadConfiguration()!.DeviceId);
      Assert.Contains("printer", persisted.GetExposedPrinterIds());
      Assert.Equal(0, fixture.ForbiddenPolls);
   }



   /// <summary>Reads the snapshot the service replies with to a profile command.</summary>
   private static AgentSnapshot Snapshot(string reply) => JsonSerializer.Deserialize<AgentSnapshot>(reply, PrintApiClient.JsonOptions) ?? throw new IOException("Snapshot missing.");



   /// <summary>The first printer the agent published in an inventory with a full-length (4,096 character) description, or null when none has arrived yet.</summary>
   private static PrinterInfo? DescribedPrinter(RealtimeRuntimeFixture fixture)
      => fixture.Messages.Where(message => message.Type is "INVENTORY")
         .SelectMany(message => message.Payload.GetProperty("printers").Deserialize<PrinterInfo[]>(PrintApiClient.JsonOptions) ?? [])
         .FirstOrDefault(item => item.Description?.Length == 4096);



   /// <summary>The first device profile the agent sent in a heartbeat with a full-length (4,096 character) description, or null when none has arrived yet.</summary>
   private static DeviceProfile? DescribedDevice(RealtimeRuntimeFixture fixture)
      => fixture.Messages.Where(message => message.Type is "HEARTBEAT" && message.Payload.TryGetProperty("profile", out JsonElement profile) && profile.ValueKind == JsonValueKind.Object)
         .Select(message => message.Payload.GetProperty("profile").Deserialize<DeviceProfile>(PrintApiClient.JsonOptions))
         .FirstOrDefault(profile => profile?.Description?.Length == 4096);
}
