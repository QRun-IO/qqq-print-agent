/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;


namespace Qqq.PrintAgent.Core.Tests;

/// <summary>The wire shape of operator metadata: heartbeat and printer inventory carry names, descriptions and locations in a way the server can distinguish "not set" from "cleared", without ever replacing the physical queue identity.</summary>
public sealed class ProfileProtocolTests
{
   /// <summary>A heartbeat with no profile keeps the original body (no profile member at all). A profile with an empty description sends that empty value, so the server clears it, while an unset location is omitted so the server leaves it alone.</summary>
   [Fact]
   public async Task HeartbeatPreservesLegacyShapeAndTransmitsExplicitClears()
   {
      using CaptureHandler handler = new();
      using PrintApiClient api = new("https://synthetic.invalid", "synthetic-token", handler);
      await api.HeartbeatAsync(CancellationToken.None);
      Assert.False(handler.Requests[0].TryGetProperty("profile", out _));
      await api.HeartbeatAsync(new DeviceProfile("Packing station", "", null), CancellationToken.None);
      JsonElement profile = handler.Requests[1].GetProperty("profile");
      Assert.Equal("Packing station", profile.GetProperty("name").GetString());
      Assert.Equal("", profile.GetProperty("description").GetString());
      Assert.False(profile.TryGetProperty("location", out _));
   }



   /// <summary>The inventory sends the Windows queue name (up to 1,024 characters) alongside separate display name, description and location fields, and the stable printer id survives a serialization round trip, so a friendly label never replaces the identity jobs are dispatched against.</summary>
   [Fact]
   public async Task InventoryKeepsPhysicalQueueIdentitySeparateFromFriendlyMetadata()
   {
      using CaptureHandler handler = new();
      using PrintApiClient api = new("https://synthetic.invalid", "synthetic-token", handler);
      PrinterInfo printer = new("stable-id", new string('q', 1024), "driver", "port", "READY", ["ZPL"], new string('n', 512), new string('d', 4096), "Dock 2");
      await api.InventoryAsync([printer], CancellationToken.None);
      JsonElement sent = handler.Requests[0].GetProperty("printers")[0];
      Assert.Equal(printer.Name, sent.GetProperty("name").GetString());
      Assert.Equal(printer.DisplayName, sent.GetProperty("displayName").GetString());
      Assert.Equal(printer.Description, sent.GetProperty("description").GetString());
      Assert.Equal(printer.Location, sent.GetProperty("location").GetString());
      PrinterInfo roundTrip = sent.Deserialize<PrinterInfo>(PrintApiClient.JsonOptions)!;
      Assert.Equal(printer.PrinterId, roundTrip.PrinterId);
      Assert.Equal(printer.Name, roundTrip.Name);
      Assert.Equal(printer.DisplayName, roundTrip.DisplayName);
   }



   /// <summary>Fake server that records each request body as parsed JSON and replies with an empty object.</summary>
   private sealed class CaptureHandler : HttpMessageHandler
   {
      internal List<JsonElement> Requests { get; } = [];



      /// <summary>Clones the parsed body before the request content is disposed so tests can inspect it afterwards.</summary>
      protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
         Requests.Add(document.RootElement.Clone());
         return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
      }
   }
}
