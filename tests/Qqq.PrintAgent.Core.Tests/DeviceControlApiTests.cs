/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>The client side of the device control poll: it accepts the server's published sample reply, tells the server which control version and capabilities it supports, and refuses any reply that does not state the device's desired state.</summary>
public sealed class DeviceControlApiTests
{
   /// <summary>The shared sample reply from the server project parses (paused state, two commands), and the request the client sends names control version 1 and exactly two capabilities, so both sides agree on the wire format.</summary>
   [Fact]
   public async Task SharedControlFixtureIsAcceptedAndCapabilitiesAdvertised()
   {
      string body = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "control-poll.response.json"));
      using Handler handler = new(body);
      using PrintApiClient api = new("https://example.invalid", "fixture", handler);
      DeviceControlPoll response = (await api.ControlPollAsync(0, CancellationToken.None))!;
      Assert.True(response.DesiredState.DispatchPaused);
      Assert.Equal(2, response.Commands.Count);
      using var request = JsonDocument.Parse(handler.RequestBody!);
      Assert.Equal(1, request.RootElement.GetProperty("controlVersion").GetInt32());
      Assert.Equal(2, request.RootElement.GetProperty("capabilities").GetArrayLength());
   }



   /// <summary>An empty object, a null body, or an unsupported control version is rejected as invalid data; a missing desired state must never be read as "connected, not paused", which would let a paused device print.</summary>
   [Theory]
   [InlineData("{}")]
   [InlineData("null")]
   [InlineData("{\"controlVersion\":2}")]
   public async Task MissingOrInvalidControlStateNeverMeansConnected(string body)
   {
      using PrintApiClient api = new("https://example.invalid", "fixture", new Handler(body));
      await Assert.ThrowsAsync<InvalidDataException>(() => api.ControlPollAsync(0, CancellationToken.None));
   }



   /// <summary>Fake server that replies with the configured body and remembers the request body it received.</summary>
   private sealed class Handler(string body) : HttpMessageHandler
   {
      internal string? RequestBody { get; private set; }



      /// <summary>Stores the request text, then returns the configured JSON with status 200.</summary>
      protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
         return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
      }
   }
}
