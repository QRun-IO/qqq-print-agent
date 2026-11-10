/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.WebSockets;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Testing;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Tests of the scripted server itself, without a service: a plain WebSocket client and plain HTTP requests stand in for the
/// agent, so what is shown is the server's own state and nothing a runtime adds to it.
/// </summary>
public sealed class ScriptedPrintServerTests
{
   /// <summary>
   /// Two scripted servers stand side by side, as they will behind one service. An agent connects to the first only, has its
   /// applied state acknowledged there, claims the first server's one job and reads its discovery. The second server is asked
   /// the same things over HTTP. Each answers from its own state: the first hands out its job under its own settings and
   /// printing version; the second, which no agent connected to, refuses the claim as one made without a connection and
   /// counts it as forbidden, has no job, no message and no claim, and advertises its own version. Nothing done to one
   /// shows on the other, in either direction.
   /// </summary>
   [Fact]
   public async Task ScriptedPrintServer_TwoInstances_DoNotShareState()
   {
      using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
      await using ScriptedPrintServer first = new();
      await using ScriptedPrintServer second = new() { PrintingVersion = "2.4.0" };
      second.Settings = second.Settings with { Revision = 9 };
      first.AddJob("job");
      using HttpMessageInvoker toFirst = new(first.CreateHandler());
      using HttpMessageInvoker toSecond = new(second.CreateHandler());
      using ClientWebSocket agent = new();
      Assert.NotEqual(first.Origin, second.Origin);
      RealtimeEnvelope welcome = await ConnectAsync(agent, first, limit.Token);
      Assert.Equal(1, welcome.Payload.GetProperty("settings").GetProperty("revision").GetInt64());
      Assert.Equal(3, first.LastHelloSettingsRevision);
      Assert.Equal(0, second.LastHelloSettingsRevision);
      Assert.Equal("synthetic", Assert.Single(first.LastHelloCapabilities));
      Assert.Empty(second.LastHelloCapabilities);
      _ = await Assert.ThrowsAsync<InvalidOperationException>(() => toSecond.SendAsync(Request(second, HttpMethod.Post, "device/jobs/claim"), limit.Token));
      using HttpResponseMessage claimed = await toFirst.SendAsync(Request(first, HttpMethod.Post, "device/jobs/claim"), limit.Token);
      Assert.Contains("\"jobId\":\"job\"", await claimed.Content.ReadAsStringAsync(limit.Token), StringComparison.Ordinal);
      Assert.Equal((1, 0), (first.Claims, first.ForbiddenPolls));
      Assert.Equal((0, 1), (second.Claims, second.ForbiddenPolls));
      Assert.Equal("STATE_APPLIED", Assert.Single(first.Messages).Type);
      Assert.Empty(second.Messages);
      Assert.Empty(second.Queue);
      Assert.Empty(second.Documents);
      Assert.Equal("job", Assert.Single(first.Documents).Key);
      Assert.Contains("\"version\":\"2.0.0\"", await DiscoveryAsync(toFirst, first, limit.Token), StringComparison.Ordinal);
      Assert.Contains("\"version\":\"2.4.0\"", await DiscoveryAsync(toSecond, second, limit.Token), StringComparison.Ordinal);
      Assert.Equal((1, 2), (first.DiscoveryRequests, first.Requests));
      Assert.Equal((1, 2), (second.DiscoveryRequests, second.Requests));
   }



   /// <summary>
   /// Plays the agent's opening on one server: opens the realtime socket, says HELLO, reads WELCOME, reports the desired state
   /// as applied and reads the acknowledgement, after which that server takes claims. Returns the WELCOME it was sent.
   /// </summary>
   private static async Task<RealtimeEnvelope> ConnectAsync(ClientWebSocket agent, ScriptedPrintServer server, CancellationToken token)
   {
      agent.Options.AddSubProtocol("qqq-printing.v1");
      Task<ScriptedPrintServer.Connection> accepting = server.AcceptAsync(new DeviceDesiredState(1, false, "CONNECTED"), loseSpoolAck: false, token);
      await agent.ConnectAsync(new UriBuilder(server.Origin) { Scheme = "ws", Path = "/qqq/v1/qbit/printing/device/realtime" }.Uri, token);
      await LoopbackWebSocketPeer.SendAsync(agent, "HELLO", new { lastSettingsRevision = 3, capabilities = new[] { "synthetic" } }, token);
      RealtimeEnvelope welcome = await LoopbackWebSocketPeer.ReceiveAsync(agent, token);
      Assert.Equal("WELCOME", welcome.Type);
      _ = await accepting;
      await LoopbackWebSocketPeer.SendAsync(agent, "STATE_APPLIED", new { lastAppliedRevision = 1 }, token);
      Assert.Equal("ACK", (await LoopbackWebSocketPeer.ReceiveAsync(agent, token)).Type);
      return welcome;
   }



   /// <summary>Reads one server's version discovery, the document that names its printing version.</summary>
   private static async Task<string> DiscoveryAsync(HttpMessageInvoker http, ScriptedPrintServer server, CancellationToken token)
   {
      using HttpRequestMessage request = new(HttpMethod.Get, new Uri(server.Origin, "/qqq/v1/version"));
      using HttpResponseMessage response = await http.SendAsync(request, token);
      return await response.Content.ReadAsStringAsync(token);
   }



   /// <summary>A request to one of a server's printing routes, addressed to that server.</summary>
   private static HttpRequestMessage Request(ScriptedPrintServer server, HttpMethod method, string route)
      => new(method, new Uri(server.Origin, "/qqq/v1/qbit/printing/" + route));
}
