/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;


namespace Qqq.PrintAgent.Core.Tests;

/// <summary>The wire shape of operator metadata: heartbeat and printer inventory carry names, descriptions and locations in a way the server can distinguish "not set" from "cleared", without ever replacing the physical queue identity.</summary>
public sealed class ProfileProtocolTests
{
   /// <summary>
   /// The heartbeat frame, in the shape the service sends it. With no profile it has no profile member at all. A profile
   /// with an empty description sends that empty value, so the server clears it, while an unset location is omitted so
   /// the server leaves it alone.
   /// </summary>
   [Fact]
   public async Task HeartbeatOmitsAnUnsetProfileAndTransmitsExplicitClears()
   {
      using var socket = new ScriptedWebSocket();
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      using var stop = new CancellationTokenSource();
      Task run = channel.RunAsync(_ => { }, stop.Token);
      await channel.SendAsync("HEARTBEAT", new { agentVersion = PrintApiClient.AgentVersion, profile = (DeviceProfile?)null });
      JsonElement payload = (await socket.NextSentAsync()).GetProperty("payload");
      Assert.Equal(PrintApiClient.AgentVersion, payload.GetProperty("agentVersion").GetString());
      Assert.False(payload.TryGetProperty("profile", out _));
      await channel.SendAsync("HEARTBEAT", new { agentVersion = PrintApiClient.AgentVersion, profile = new DeviceProfile("Packing station", "", null) });
      JsonElement profile = (await socket.NextSentAsync()).GetProperty("payload").GetProperty("profile");
      Assert.Equal("Packing station", profile.GetProperty("name").GetString());
      Assert.Equal("", profile.GetProperty("description").GetString());
      Assert.False(profile.TryGetProperty("location", out _));
      await stop.CancelAsync();
      await run.WaitAsync(TimeSpan.FromSeconds(10));
   }



   /// <summary>The inventory frame sends the Windows queue name (up to 1,024 characters) alongside separate display name, description and location fields, and the stable printer id survives a serialization round trip, so a friendly label never replaces the identity jobs are dispatched against.</summary>
   [Fact]
   public async Task InventoryKeepsPhysicalQueueIdentitySeparateFromFriendlyMetadata()
   {
      using var socket = new ScriptedWebSocket();
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      using var stop = new CancellationTokenSource();
      Task run = channel.RunAsync(_ => { }, stop.Token);
      using PrintApiClient api = new PrintApiClient("https://synthetic.invalid").WithRealtime(channel, new RealtimeSettings());
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
