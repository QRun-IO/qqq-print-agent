/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;


namespace Qqq.PrintAgent.Core.Tests;

public sealed class ProfileProtocolTests
{
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

   private sealed class CaptureHandler : HttpMessageHandler
   {
      internal List<JsonElement> Requests { get; } = [];

      protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
         Requests.Add(document.RootElement.Clone());
         return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
      }
   }
}
