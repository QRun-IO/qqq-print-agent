/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.WebSockets;
using Qqq.PrintAgent.Testing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// The realtime channel's tests run against a hand-written socket, so what they prove about a connection that ends is
/// only as true as that fake. These tests end the fake and the platform's client socket, the type the service really
/// uses, in the same way and require the same observations from both: what a receive that was waiting reports, the
/// state the socket is left in, and what a later receive and a later send report. The platform socket is connected to
/// a loopback peer inside the test; no server is involved.
/// </summary>
public sealed class ScriptedWebSocketTests
{
   private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);



   /// <summary>Aborting, disposing, or closing the sending side leaves the fake exactly where it leaves the platform's client socket.</summary>
   [Theory]
   [InlineData("abort", "waiting receive: OperationCanceledException; state: Aborted; receive: ObjectDisposedException; send: ObjectDisposedException")]
   [InlineData("dispose", "waiting receive: WebSocketException ConnectionClosedPrematurely; state: Closed; receive: ObjectDisposedException; send: ObjectDisposedException")]
   [InlineData("close-output", "state: CloseSent; send: WebSocketException InvalidState")]
   public async Task FakeEndsTheWayThePlatformSocketDoes(string ending, string observed)
   {
      using var peer = new LoopbackWebSocketPeer();
      using var platform = new ClientWebSocket();
      platform.Options.AddSubProtocol("qqq-printing.v1");
      Task<WebSocket> accepting = peer.AcceptAsync(CancellationToken.None);
      await platform.ConnectAsync(new Uri($"ws://127.0.0.1:{peer.Origin.Port}/qqq/v1/qbit/printing/device/realtime"), CancellationToken.None).WaitAsync(Patience);
      using WebSocket server = await accepting.WaitAsync(Patience);
      using var fake = new ScriptedWebSocket();
      Assert.Equal(observed, await EndAndObserveAsync(platform, ending));
      Assert.Equal(observed, await EndAndObserveAsync(fake, ending));
   }



   /// <summary>A frame queued before the fake was aborted is not handed out afterwards, and nothing can be queued for a socket that has ended.</summary>
   [Fact]
   public async Task FrameQueuedBeforeTheEndIsNeverHandedOut()
   {
      using var fake = new ScriptedWebSocket();
      fake.Deliver("PING", "m-1", new { });
      fake.Abort();
      await Assert.ThrowsAsync<ObjectDisposedException>(() => fake.ReceiveAsync(new ArraySegment<byte>(new byte[256]), CancellationToken.None));
      Assert.Equal(1, fake.Aborts);
   }



   /// <summary>A send the test's hook is holding fails if the socket ends meanwhile, instead of being recorded as sent.</summary>
   [Fact]
   public async Task SendHeldWhileTheSocketEndsFails()
   {
      var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      using var fake = new ScriptedWebSocket { Sending = _ => held.Task };
      Task send = fake.SendAsync(new ArraySegment<byte>("{}"u8.ToArray()), WebSocketMessageType.Text, true, CancellationToken.None);
      fake.Abort();
      held.SetResult();
      await Assert.ThrowsAsync<ObjectDisposedException>(() => send.WaitAsync(Patience));
   }



   /// <summary>Ends a socket in the given way and reports what was observed, in the same words for any socket.</summary>
   private static async Task<string> EndAndObserveAsync(WebSocket socket, string ending)
   {
      var observed = new List<string>();
      bool closeOutput = string.Equals(ending, "close-output", StringComparison.Ordinal);
      Task? waiting = closeOutput ? null : socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), CancellationToken.None);
      switch(ending)
      {
         case "abort":
            socket.Abort();
            break;
         case "dispose":
            socket.Dispose();
            break;
         default:
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            break;
      }
      if(waiting is not null)
      { observed.Add("waiting receive: " + await OutcomeAsync(() => waiting)); }
      observed.Add("state: " + socket.State);
      if(!closeOutput)
      { observed.Add("receive: " + await OutcomeAsync(() => socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), CancellationToken.None))); }
      observed.Add("send: " + await OutcomeAsync(() => socket.SendAsync(new ArraySegment<byte>("{}"u8.ToArray()), WebSocketMessageType.Text, true, CancellationToken.None)));
      return string.Join("; ", observed);
   }



   /// <summary>Names how an operation ended: completed, a WebSocket error with its code, or a cancellation; anything else fails the test.</summary>
   private static async Task<string> OutcomeAsync(Func<Task> operation)
   {
      try
      {
         await operation().WaitAsync(Patience);
         return "completed";
      }
      catch(WebSocketException exception)
      { return "WebSocketException " + exception.WebSocketErrorCode; }
      catch(OperationCanceledException)
      { return nameof(OperationCanceledException); }
      catch(ObjectDisposedException)
      { return nameof(ObjectDisposedException); }
   }
}
