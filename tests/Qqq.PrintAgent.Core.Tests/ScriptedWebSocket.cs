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
/// touches the network, so each malformed shape can be delivered exactly. Where the channel depends on what a socket
/// does when it ends, the fake does what the platform socket does: aborting or disposing it faults a receive that is
/// waiting, and nothing can be received or sent on it afterwards.
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



   /// <summary>Called with every frame the channel sent, after it is recorded; a test whose server answers by itself uses it to deliver the reply.</summary>
   internal Func<JsonElement, Task>? Sent { get; set; }



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



   /// <summary>
   /// Counts the abort and, as the platform socket does, moves an unfinished socket to the aborted state and ends a
   /// receive that is waiting with a cancellation named "Aborted". A socket that was already closed stays closed.
   /// </summary>
   public override void Abort()
   {
      Interlocked.Increment(ref _aborts);
      if(_state is not (WebSocketState.Closed or WebSocketState.Aborted))
      { _state = WebSocketState.Aborted; }
      _incoming.Writer.TryComplete(new OperationCanceledException(nameof(WebSocketState.Aborted)));
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



   /// <summary>
   /// Marks the socket disposed and, unless it was aborted first, closed. A receive that is waiting ends as it does on
   /// the platform socket when the connection goes away underneath it: closed before the close handshake finished.
   /// </summary>
   public override void Dispose()
   {
      Volatile.Write(ref _disposed, true);
      if(_state != WebSocketState.Aborted)
      { _state = WebSocketState.Closed; }
      _incoming.Writer.TryComplete(new WebSocketException(WebSocketError.ConnectionClosedPrematurely));
   }



   /// <summary>
   /// Hands the channel the next queued frame, waiting until the test queues one, the channel cancels, or the socket is
   /// aborted or disposed. A socket that has ended hands out nothing, not even frames queued before it ended.
   /// </summary>
   public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
   {
      ThrowIfEnded();
      if(!await _incoming.Reader.WaitToReadAsync(cancellationToken))
      { throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely); }
      ThrowIfEnded();
      Assert.True(_incoming.Reader.TryRead(out (byte[] Bytes, WebSocketMessageType Type, bool End) frame));
      (byte[] bytes, WebSocketMessageType type, bool end) = frame;
      Assert.True(bytes.Length <= buffer.Count, "A scripted frame must fit the channel's receive buffer; deliver a large message as fragments.");
      bytes.CopyTo(buffer.Array!, buffer.Offset);
      return new WebSocketReceiveResult(bytes.Length, type, end);
   }



   /// <summary>
   /// Runs the test's hook, then records the frame the channel sent and hands it to the test's answering hook. A socket
   /// that has ended, before the send or while the first hook held it, accepts nothing: the send fails as it does on the
   /// platform socket.
   /// </summary>
   public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
   {
      ThrowIfEnded();
      if(Sending is not null)
      { await Sending(cancellationToken); }
      ThrowIfEnded();
      Assert.Equal(WebSocketMessageType.Text, messageType);
      Assert.True(endOfMessage);
      string frame = Encoding.UTF8.GetString(buffer);
      Assert.True(_sent.Writer.TryWrite(frame));
      if(Sent is not null)
      {
         using var document = JsonDocument.Parse(frame);
         await Sent(document.RootElement.Clone());
      }
   }



   /// <summary>
   /// Refuses an operation on a socket that is no longer open, the way the platform's client socket does: one that was
   /// aborted or disposed reports that it is disposed, and one that has sent its close reports an invalid state.
   /// </summary>
   private void ThrowIfEnded()
   {
      ObjectDisposedException.ThrowIf(Disposed || Aborts > 0, this);
      if(_state != WebSocketState.Open)
      { throw new WebSocketException(WebSocketError.InvalidState, $"The WebSocket is in an invalid state ('{_state}') for this operation."); }
   }
}
