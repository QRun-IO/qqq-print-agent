/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// A hand-written fake WebSocket for the realtime channel: the test plays the server by queueing the frames the channel
/// will receive (whole messages, fragments, binary frames, a close) and reads back every frame the channel sent. Nothing
/// touches the network, so each malformed shape can be delivered exactly.
/// </summary>
internal sealed class ScriptedWebSocket : WebSocket
{
   private readonly Channel<(byte[] Bytes, WebSocketMessageType Type, bool End)> _incoming = Channel.CreateUnbounded<(byte[], WebSocketMessageType, bool)>();
   private readonly Channel<string> _sent = Channel.CreateUnbounded<string>();
   private WebSocketState _state = WebSocketState.Open;
   private WebSocketCloseStatus? _closeStatus;
   private string? _closeDescription;
   private int _aborts;
   private bool _disposed;



   /// <summary>Called for every frame the channel sends, before it is recorded; a test uses it to stall or fail the send.</summary>
   internal Func<CancellationToken, Task>? Sending { get; set; }



   /// <summary>How many times the channel aborted the socket.</summary>
   internal int Aborts => Volatile.Read(ref _aborts);



   /// <summary>True once the channel disposed the socket.</summary>
   internal bool Disposed => Volatile.Read(ref _disposed);



   /// <summary>Every graceful close requested of the socket, in order.</summary>
   internal List<string> Closes { get; } = [];



   /// <inheritdoc/>
   public override WebSocketCloseStatus? CloseStatus => _closeStatus;



   /// <inheritdoc/>
   public override string? CloseStatusDescription => _closeDescription;



   /// <inheritdoc/>
   public override WebSocketState State => _state;



   /// <inheritdoc/>
   public override string? SubProtocol => "qqq-printing.v1";



   /// <summary>Queues one complete text message for the channel to receive.</summary>
   internal void Deliver(string json) => DeliverFrame(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, end: true);



   /// <summary>Queues one protocol envelope with the given type, message id and payload.</summary>
   internal void Deliver(string type, string messageId, object payload) => Deliver(JsonSerializer.Serialize(new { type, messageId, payload }));



   /// <summary>Queues one frame exactly as given, for fragmented, binary and oversized messages.</summary>
   internal void DeliverFrame(byte[] bytes, WebSocketMessageType type, bool end) => Assert.True(_incoming.Writer.TryWrite((bytes, type, end)));



   /// <summary>Queues the server's close frame.</summary>
   internal void DeliverClose() => DeliverFrame([], WebSocketMessageType.Close, end: true);



   /// <summary>Drops the connection: once the queued frames are read, the next receive fails as a socket does when its peer vanishes.</summary>
   internal void Drop() => _incoming.Writer.TryComplete();



   /// <summary>Returns the next frame the channel sent, as text; the ten second bound only stops a broken test from hanging.</summary>
   internal async Task<JsonElement> NextSentAsync()
   {
      using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
      string frame = await _sent.Reader.ReadAsync(limit.Token);
      using var document = JsonDocument.Parse(frame);
      return document.RootElement.Clone();
   }



   /// <inheritdoc/>
   public override void Abort()
   {
      Interlocked.Increment(ref _aborts);
      _state = WebSocketState.Aborted;
   }



   /// <summary>Records a full close and marks the socket closed.</summary>
   public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
   {
      Closes.Add($"close {closeStatus} {statusDescription}");
      _closeStatus = closeStatus;
      _closeDescription = statusDescription;
      _state = WebSocketState.Closed;
      return Task.CompletedTask;
   }



   /// <summary>Records a close of the sending side only.</summary>
   public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
   {
      Closes.Add($"close-output {closeStatus} {statusDescription}");
      _state = WebSocketState.CloseSent;
      return Task.CompletedTask;
   }



   /// <summary>Marks the socket disposed and closed.</summary>
   public override void Dispose()
   {
      Volatile.Write(ref _disposed, true);
      _state = WebSocketState.Closed;
   }



   /// <summary>Hands the channel the next queued frame, waiting until the test queues one or the channel cancels.</summary>
   public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
   {
      if(!await _incoming.Reader.WaitToReadAsync(cancellationToken))
      { throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely); }
      Assert.True(_incoming.Reader.TryRead(out (byte[] Bytes, WebSocketMessageType Type, bool End) frame));
      (byte[] bytes, WebSocketMessageType type, bool end) = frame;
      Assert.True(bytes.Length <= buffer.Count, "A scripted frame must fit the channel's receive buffer; deliver a large message as fragments.");
      bytes.CopyTo(buffer.Array!, buffer.Offset);
      return new WebSocketReceiveResult(bytes.Length, type, end);
   }



   /// <summary>Runs the test's hook, then records the frame the channel sent.</summary>
   public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
   {
      if(Sending is not null)
      { await Sending(cancellationToken); }
      Assert.Equal(WebSocketMessageType.Text, messageType);
      Assert.True(endOfMessage);
      Assert.True(_sent.Writer.TryWrite(Encoding.UTF8.GetString(buffer)));
   }
}
