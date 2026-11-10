/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.WebSockets;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Testing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// What a send is told when the realtime channel closes under it. The caller that later reports why the connection ended
/// must be able to tell "the connection was gone" from a failure that is a reason in itself, and to trace the first back to
/// what closed the channel.
/// </summary>
public sealed class RealtimeChannelClosureTests
{
   /// <summary>
   /// A send is waiting for its acknowledgement when the reader rejects a frame. The channel fails the send with the
   /// channel-closed type, and that failure carries the reader's as its cause. The channel's own run ends with the reader's
   /// failure itself.
   /// </summary>
   [Fact]
   public async Task SendWaitingForAcknowledgementFailsWithWhatEndedTheChannelAsItsCause()
   {
      using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
      using var peer = new LoopbackWebSocketPeer();
      using var client = new ClientWebSocket();
      using WebSocket server = await ConnectAsync(peer, client, guard.Token);
      using var channel = new PrintingRealtimeChannel(client, new RealtimeSettings());
      var rejected = new InvalidDataException("Synthetic rejected frame.");
      Task running = channel.RunAsync(_ => throw rejected, guard.Token);
      Task pending = channel.SendAsync("INVENTORY", new { printers = Array.Empty<object>() }, acknowledged: true, token: guard.Token);
      RealtimeEnvelope sent = await LoopbackWebSocketPeer.ReceiveAsync(server, guard.Token);
      Assert.Equal("INVENTORY", sent.Type);
      await LoopbackWebSocketPeer.SendAsync(server, "PRINTERS", new { nothing = true }, guard.Token);
      RealtimeChannelClosedException failed = await Assert.ThrowsAsync<RealtimeChannelClosedException>(() => pending);
      Assert.Contains("closed before acknowledgement", failed.Message, StringComparison.Ordinal);
      Assert.Same(rejected, failed.InnerException);
      Assert.Same(rejected, await Assert.ThrowsAsync<InvalidDataException>(() => running));
   }



   /// <summary>
   /// When the server closes the socket in an orderly way, the reader ends with that closure, and a send that was waiting
   /// carries the closure as its cause, so it is still an I/O failure a caller's existing handler catches.
   /// </summary>
   [Fact]
   public async Task SendWaitingWhenTheServerClosesCarriesTheClosure()
   {
      using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
      using var peer = new LoopbackWebSocketPeer();
      using var client = new ClientWebSocket();
      using WebSocket server = await ConnectAsync(peer, client, guard.Token);
      using var channel = new PrintingRealtimeChannel(client, new RealtimeSettings());
      Task running = channel.RunAsync(_ => { }, guard.Token);
      Task pending = channel.SendAsync("INVENTORY", new { printers = Array.Empty<object>() }, acknowledged: true, token: guard.Token);
      _ = await LoopbackWebSocketPeer.ReceiveAsync(server, guard.Token);
      await server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", guard.Token);
      IOException failed = await Assert.ThrowsAnyAsync<IOException>(() => pending);
      _ = Assert.IsType<RealtimeChannelClosedException>(failed);
      IOException closure = await Assert.ThrowsAsync<IOException>(() => running);
      Assert.Equal("Server closed the realtime connection.", closure.Message);
      Assert.Same(closure, failed.InnerException);
   }



   /// <summary>A send attempted after the channel has closed fails with the channel-closed type too, and has no cause to carry: it only found the connection gone.</summary>
   [Fact]
   public async Task SendAfterTheChannelClosedFailsWithTheChannelClosedType()
   {
      using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
      using var peer = new LoopbackWebSocketPeer();
      using var client = new ClientWebSocket();
      using WebSocket server = await ConnectAsync(peer, client, guard.Token);
      using var channel = new PrintingRealtimeChannel(client, new RealtimeSettings());
      Task running = channel.RunAsync(_ => { }, guard.Token);
      await server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", guard.Token);
      _ = await Assert.ThrowsAsync<IOException>(() => running);
      RealtimeChannelClosedException failed = await Assert.ThrowsAsync<RealtimeChannelClosedException>(() => channel.SendAsync("PING", new { }, token: guard.Token));
      Assert.Null(failed.InnerException);
   }



   /// <summary>Connects the client socket to the loopback peer on the realtime route and returns the server's side of it.</summary>
   private static async Task<WebSocket> ConnectAsync(LoopbackWebSocketPeer peer, ClientWebSocket client, CancellationToken token)
   {
      client.Options.AddSubProtocol("qqq-printing.v1");
      Task<WebSocket> accepting = peer.AcceptAsync(token);
      await client.ConnectAsync(new Uri("ws://127.0.0.1:" + peer.Origin.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/qqq/v1/qbit/printing/device/realtime"), token);
      return await accepting;
   }
}
