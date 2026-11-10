/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;


namespace Qqq.PrintAgent.Core.Tests;

/// <summary>The wire shape of operator metadata: a device profile and the printer inventory carry names, descriptions and locations in a way the server can distinguish "not set" from "cleared", without ever replacing the physical queue identity.</summary>
public sealed class ProfileProtocolTests
{
   /// <summary>
   /// How a device profile is written with the options every frame and request is written with. An empty description is
   /// written as the empty value, so the server clears it; an unset location is left out, so the server leaves it alone;
   /// and a profile with nothing set is an object with no members. This is the rule the heartbeat relies on, tested on
   /// the serializer: it does not read a frame the service built. The heartbeat the service sends is read in the Windows
   /// suite (AgentRuntimeProfileTests).
   /// </summary>
   [Fact]
   public void DeviceProfileKeepsAnExplicitClearAndOmitsWhatIsUnset()
   {
      JsonElement profile = JsonSerializer.SerializeToElement(new DeviceProfile("Packing station", "", null), PrintApiClient.JsonOptions);
      Assert.Equal("Packing station", profile.GetProperty("name").GetString());
      Assert.Equal("", profile.GetProperty("description").GetString());
      Assert.False(profile.TryGetProperty("location", out _));
      Assert.Equal("{}", JsonSerializer.Serialize(new DeviceProfile(), PrintApiClient.JsonOptions));
   }



   /// <summary>The inventory frame sends the Windows queue name (up to 1,024 characters) alongside separate display name, description and location fields, and the stable printer id survives a serialization round trip, so a friendly label never replaces the identity jobs are dispatched against.</summary>
   [Fact]
   public async Task InventoryKeepsPhysicalQueueIdentitySeparateFromFriendlyMetadata()
   {
      using var session = new FakeSessionClient();
      using var socket = new ScriptedWebSocket();
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      using var stop = new CancellationTokenSource();
      Task run = channel.RunAsync(_ => { }, stop.Token);
      using PrintApiClient api = PrintApiClient.ForSession(session).WithRealtime(channel, new RealtimeSettings());
      PrinterInfo printer = new("stable-id", new string('q', 1024), "driver", "port", "READY", ["ZPL"], new string('n', 512), new string('d', 4096), "Dock 2");
      Task inventory = api.InventoryAsync([printer], CancellationToken.None);
      JsonElement frame = await socket.NextSentAsync();
      socket.Deliver("ACK", "ack-1", new { acknowledgedMessageId = frame.GetProperty("messageId").GetString() });
      await inventory.WaitAsync(TimeSpan.FromSeconds(10));
      JsonElement sent = frame.GetProperty("payload").GetProperty("printers")[0];
      Assert.Equal(printer.Name, sent.GetProperty("name").GetString());
      Assert.Equal(printer.DisplayName, sent.GetProperty("displayName").GetString());
      Assert.Equal(printer.Description, sent.GetProperty("description").GetString());
      Assert.Equal(printer.Location, sent.GetProperty("location").GetString());
      PrinterInfo roundTrip = sent.Deserialize<PrinterInfo>(PrintApiClient.JsonOptions)!;
      Assert.Equal(printer.PrinterId, roundTrip.PrinterId);
      Assert.Equal(printer.Name, roundTrip.Name);
      Assert.Equal(printer.DisplayName, roundTrip.DisplayName);
      await stop.CancelAsync();
      await run.WaitAsync(TimeSpan.FromSeconds(10));
   }
}
