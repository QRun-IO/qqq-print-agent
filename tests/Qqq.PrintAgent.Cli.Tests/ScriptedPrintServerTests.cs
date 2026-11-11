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
/// agent, so what is shown is the server's own state and nothing a runtime adds to it. They stand two servers side by side,
/// as they will be behind one service, and show that what is done to one does not show on the other.
/// </summary>
public sealed class ScriptedPrintServerTests
{
   /// <summary>The answer to a claim that found no job.</summary>
   private const string NoJob = "{}";



   /// <summary>
   /// Each server answers from its own queue, its own claimed job, its own acknowledged connection and its own counters. The
   /// first server has a job and an agent; the second has neither, so its queue is empty while the first's holds the job, and a
   /// claim made to it is refused as one made without a connection and counted as forbidden. The first hands out its job.
   /// Then the second gets an agent and a job of its own: its claim returns that job, not the one the first server still
   /// holds as claimed, and the first, asked again, still returns its own. Messages, HELLO details, documents, settings and
   /// the advertised printing version stay with the server they belong to.
   /// </summary>
   [Fact]
   public async Task ScriptedPrintServer_TwoInstances_DoNotShareState()
   {
      using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
      await using ScriptedPrintServer first = new();
      await using ScriptedPrintServer second = new() { PrintingVersion = "2.4.0" };
      second.Settings = second.Settings with { Revision = 9 };
      using HttpMessageInvoker toFirst = new(first.CreateHandler());
      using HttpMessageInvoker toSecond = new(second.CreateHandler());
      using ClientWebSocket agentOfFirst = new();
      using ClientWebSocket agentOfSecond = new();
      Assert.NotEqual(first.Origin, second.Origin);
      first.AddJob("job");
      Assert.Equal("job", Assert.Single(first.Queue).JobId);
      Assert.Empty(second.Queue);
      Assert.Equal("job", Assert.Single(first.Documents).Key);
      Assert.Empty(second.Documents);
      (RealtimeEnvelope welcome, _) = await ConnectAsync(agentOfFirst, first, settingsRevision: 3, limit.Token);
      Assert.Equal(1, welcome.Payload.GetProperty("settings").GetProperty("revision").GetInt64());
      Assert.Equal((3, 0), (first.LastHelloSettingsRevision, second.LastHelloSettingsRevision));
      Assert.Equal("synthetic", Assert.Single(first.LastHelloCapabilities));
      Assert.Empty(second.LastHelloCapabilities);
      _ = await Assert.ThrowsAsync<InvalidOperationException>(() => ClaimAsync(toSecond, second, limit.Token));
      Assert.Contains("\"jobId\":\"job\"", await ClaimAsync(toFirst, first, limit.Token), StringComparison.Ordinal);
      Assert.Equal((1, 0), (first.Claims, first.ForbiddenPolls));
      Assert.Equal((0, 1), (second.Claims, second.ForbiddenPolls));
      Assert.Equal("STATE_APPLIED", Assert.Single(first.Messages).Type);
      Assert.Empty(second.Messages);
      second.AddJob("second-job");
      (welcome, _) = await ConnectAsync(agentOfSecond, second, settingsRevision: 5, limit.Token);
      Assert.Equal(9, welcome.Payload.GetProperty("settings").GetProperty("revision").GetInt64());
      Assert.Equal((3, 5), (first.LastHelloSettingsRevision, second.LastHelloSettingsRevision));
      Assert.Contains("\"jobId\":\"second-job\"", await ClaimAsync(toSecond, second, limit.Token), StringComparison.Ordinal);
      Assert.Contains("\"jobId\":\"job\"", await ClaimAsync(toFirst, first, limit.Token), StringComparison.Ordinal);
      Assert.Equal((2, 1), (first.Claims, second.Claims));
      Assert.Equal("STATE_APPLIED", Assert.Single(second.Messages).Type);
      Assert.Contains("\"version\":\"2.0.0\"", await DiscoveryAsync(toFirst, first, limit.Token), StringComparison.Ordinal);
      Assert.Contains("\"version\":\"2.4.0\"", await DiscoveryAsync(toSecond, second, limit.Token), StringComparison.Ordinal);
      Assert.Equal((1, 3), (first.DiscoveryRequests, first.Requests));
      Assert.Equal((1, 3), (second.DiscoveryRequests, second.Requests));
   }



   /// <summary>
   /// A hold and a refusal placed on one server do not reach the other. With claims held on the first server, a claim to it
   /// arrives and waits while a claim to the second is answered at once; the held claim is answered when the hold is
   /// released. With the first server told to refuse every message, the same message is refused there with an ERROR frame
   /// and acknowledged by the second.
   /// </summary>
   [Fact]
   public async Task ScriptedPrintServer_HoldAndRefusalOnOne_DoNotReachTheOther()
   {
      using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
      await using ScriptedPrintServer first = new();
      await using ScriptedPrintServer second = new();
      using HttpMessageInvoker toFirst = new(first.CreateHandler());
      using HttpMessageInvoker toSecond = new(second.CreateHandler());
      using ClientWebSocket agentOfFirst = new();
      using ClientWebSocket agentOfSecond = new();
      _ = await ConnectAsync(agentOfFirst, first, settingsRevision: 1, limit.Token);
      _ = await ConnectAsync(agentOfSecond, second, settingsRevision: 1, limit.Token);
      first.ClaimHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      Task<string> held = ClaimAsync(toFirst, first, limit.Token);
      await first.ClaimArrived.Task.WaitAsync(limit.Token);
      Assert.Equal(NoJob, await ClaimAsync(toSecond, second, limit.Token));
      Assert.False(held.IsCompleted);
      first.ClaimHold.SetResult();
      Assert.Equal(NoJob, await held);
      first.RefuseWith = _ => "NOT_FOUND";
      Assert.Equal("ERROR", await ExchangeAsync(agentOfFirst, "STATUS", limit.Token));
      Assert.Equal("ACK", await ExchangeAsync(agentOfSecond, "STATUS", limit.Token));
   }



   /// <summary>
   /// Stopping one server stops only that server. After the first server closes its connections, its own pump has ended,
   /// while the second server's connection still acknowledges a message and its HTTP side still answers. The second
   /// server's connection ends when the second server itself is closed, and not before.
   /// </summary>
   [Fact]
   public async Task ScriptedPrintServer_ClosingOne_LeavesTheOtherAnswering()
   {
      using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
      await using ScriptedPrintServer first = new();
      await using ScriptedPrintServer second = new();
      using HttpMessageInvoker toSecond = new(second.CreateHandler());
      using ClientWebSocket agentOfFirst = new();
      using ClientWebSocket agentOfSecond = new();
      (_, ScriptedPrintServer.Connection ofFirst) = await ConnectAsync(agentOfFirst, first, settingsRevision: 1, limit.Token);
      (_, ScriptedPrintServer.Connection ofSecond) = await ConnectAsync(agentOfSecond, second, settingsRevision: 1, limit.Token);
      await first.CloseConnectionsAsync();
      Assert.True(ofFirst.Completion.IsCompleted);
      Assert.Equal("ACK", await ExchangeAsync(agentOfSecond, "STATUS", limit.Token));
      Assert.False(ofSecond.Completion.IsCompleted);
      Assert.Contains("\"version\":\"2.0.0\"", await DiscoveryAsync(toSecond, second, limit.Token), StringComparison.Ordinal);
      await second.CloseConnectionsAsync();
      Assert.True(ofSecond.Completion.IsCompleted);
   }



   /// <summary>
   /// A request that reaches a server's handler but is addressed to another server is refused and counted, and nothing else
   /// about either server changes: the handler that received it counts one misrouted request and routes nothing, and the
   /// server the request named never sees it. This is what catches a session built on the wrong server's handler. A
   /// request addressed to the handler's own server is still answered afterwards.
   /// </summary>
   [Fact]
   public async Task ScriptedPrintServer_RequestForAnotherOrigin_IsRefusedAndCounted()
   {
      using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
      await using ScriptedPrintServer first = new();
      await using ScriptedPrintServer second = new();
      using HttpMessageInvoker toFirst = new(first.CreateHandler());
      InvalidOperationException refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => DiscoveryAsync(toFirst, second, limit.Token));
      Assert.Contains(second.Origin.Authority, refusal.Message, StringComparison.Ordinal);
      Assert.Equal((1, 1, 0), (first.MisroutedRequests, first.Requests, first.DiscoveryRequests));
      Assert.Equal((0, 0, 0), (second.MisroutedRequests, second.Requests, second.DiscoveryRequests));
      Assert.Contains("\"version\":\"2.0.0\"", await DiscoveryAsync(toFirst, first, limit.Token), StringComparison.Ordinal);
      Assert.Equal((1, 2, 1), (first.MisroutedRequests, first.Requests, first.DiscoveryRequests));
   }



   /// <summary>
   /// Plays the agent's opening on one server: opens the realtime socket, says HELLO with the given settings revision, reads
   /// WELCOME, reports the desired state as applied and reads the acknowledgement, after which that server takes claims.
   /// Returns the WELCOME the agent was sent and the server's end of the connection.
   /// </summary>
   private static async Task<(RealtimeEnvelope Welcome, ScriptedPrintServer.Connection Connection)> ConnectAsync(ClientWebSocket agent, ScriptedPrintServer server, long settingsRevision, CancellationToken token)
   {
      agent.Options.AddSubProtocol("qqq-printing.v1");
      Task<ScriptedPrintServer.Connection> accepting = server.AcceptAsync(new DeviceDesiredState(1, false, "CONNECTED"), loseSpoolAck: false, token);
      await agent.ConnectAsync(new UriBuilder(server.Origin) { Scheme = "ws", Path = "/qqq/v1/qbit/printing/device/realtime" }.Uri, token);
      await LoopbackWebSocketPeer.SendAsync(agent, "HELLO", new { lastSettingsRevision = settingsRevision, capabilities = new[] { "synthetic" } }, token);
      RealtimeEnvelope welcome = await LoopbackWebSocketPeer.ReceiveAsync(agent, token);
      Assert.Equal("WELCOME", welcome.Type);
      ScriptedPrintServer.Connection connection = await accepting;
      Assert.Equal("ACK", await ExchangeAsync(agent, "STATE_APPLIED", token));
      return (welcome, connection);
   }



   /// <summary>Sends one agent message of the given type and returns the type of the frame the server answers it with.</summary>
   private static async Task<string> ExchangeAsync(ClientWebSocket agent, string type, CancellationToken token)
   {
      await LoopbackWebSocketPeer.SendAsync(agent, type, new { lastAppliedRevision = 1 }, token);
      return (await LoopbackWebSocketPeer.ReceiveAsync(agent, token)).Type;
   }



   /// <summary>Makes one claim on a server through the given handler and returns the body of the answer.</summary>
   private static async Task<string> ClaimAsync(HttpMessageInvoker http, ScriptedPrintServer server, CancellationToken token)
   {
      using HttpRequestMessage request = new(HttpMethod.Post, new Uri(server.Origin, "/qqq/v1/qbit/printing/device/jobs/claim"));
      using HttpResponseMessage response = await http.SendAsync(request, token);
      return await response.Content.ReadAsStringAsync(token);
   }



   /// <summary>Reads the version discovery addressed to a server, the document that names its printing version, through the given handler.</summary>
   private static async Task<string> DiscoveryAsync(HttpMessageInvoker http, ScriptedPrintServer server, CancellationToken token)
   {
      using HttpRequestMessage request = new(HttpMethod.Get, new Uri(server.Origin, "/qqq/v1/version"));
      using HttpResponseMessage response = await http.SendAsync(request, token);
      return await response.Content.ReadAsStringAsync(token);
   }
}
