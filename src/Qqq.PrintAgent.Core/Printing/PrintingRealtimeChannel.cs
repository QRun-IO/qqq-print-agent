/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using Qqq.PrintAgent.Core.Transport;

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// One continuously active reader and one bounded writer. Callbacks must only dispatch work, never await it. The
/// acknowledgement wait is counted on the supplied clock (the system clock by default), so a test reaches the timeout
/// by moving a clock.
/// </summary>
public sealed class PrintingRealtimeChannel(WebSocket socket, RealtimeSettings limits, ApplicationTrafficMeter? traffic = null, TimeProvider? timeProvider = null) : IDisposable
{
   private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
   private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
   private RealtimeSettings _limits = limits;
   private readonly ConcurrentDictionary<string, TaskCompletionSource> _pending = new(StringComparer.Ordinal);
   private long _queuedBytes;
   private int _queuedFrames;
   private int _closed;
   private int _disposed;



   /// <summary>True only while the channel has not been closed locally and the socket itself is still open.</summary>
   public bool IsOpen => Volatile.Read(ref _closed) == 0 && socket.State == WebSocketState.Open;



   /// <summary>Applies new server limits to messages sent from now on. They are validated first, so an out-of-range policy can never replace working limits.</summary>
   public void UpdateSettings(RealtimeSettings settings)
   {
      settings.Validate();
      Volatile.Write(ref _limits, settings);
   }



   /// <summary>
   /// Runs the reader and writer until either ends, then shuts the channel down in a fixed order: mark it closed, stop the
   /// outgoing queue, cancel both loops, abort the socket, and fail every send still waiting for an acknowledgement so its
   /// caller replays the durable record instead of waiting forever. When the reader or writer ended with a failure, each of
   /// those failed sends carries it as its cause, so whoever reports the send also reports why the channel closed.
   /// <para>
   /// How the method ends says why the channel ended. It returns normally when the channel was ended on purpose: the
   /// caller cancelled the token it passed in, or the channel was disposed while it ran. It throws when the channel ended
   /// by itself, with what the reader failed with (a frame it rejected, a socket the server closed, a callback that
   /// threw) or, when the reader did not fail, what the writer failed with. A callback that gives up by throwing a
   /// cancellation while the caller's token is not cancelled ends the method as that cancellation, because it was not
   /// the caller who asked to stop.
   /// </para>
   /// <para>
   /// A socket that is being ended refuses the next call on it, and a loop that is about to use the socket at that
   /// moment fails on it instead of seeing the cancellation. The socket is ended twice over on a deliberate stop: the
   /// platform socket aborts itself as soon as the token of a call in flight is cancelled, and this shutdown aborts it
   /// again. A failure of that kind (the socket is disposed, or in an invalid state) is the stop itself and is not
   /// reported when the caller had cancelled, or when the loop had not failed before this shutdown began. Any other
   /// failure is reported whenever it arrives, even beside a cancellation.
   /// </para>
   /// </summary>
   public async Task RunAsync(Action<RealtimeEnvelope> received, CancellationToken token)
   {
      using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
      Task reader = ReadAsync(received, lifetime.Token);
      Task writer = WriteAsync(lifetime.Token);
      Task ended = await Task.WhenAny(reader, writer).ConfigureAwait(false);

      ////////////////////////////////////////////////////////////////////////
      // read before the teardown below: whether the channel was disposed,  //
      // so one that failed first and was disposed a moment later still     //
      // reports its failure; and which loop had already failed while the   //
      // caller had not cancelled, because only such a failure can be told  //
      // from the socket refusing a call as it is being ended               //
      ////////////////////////////////////////////////////////////////////////
      bool disposed = Volatile.Read(ref _disposed) != 0;
      bool callerCancelled = token.IsCancellationRequested;
      bool readerFailedFirst = reader.IsFaulted && !callerCancelled;
      bool writerFailedFirst = writer.IsFaulted && !callerCancelled;
      Interlocked.Exchange(ref _closed, 1);
      _outgoing.Writer.TryComplete();
      await lifetime.CancelAsync().ConfigureAwait(false);
      socket.Abort();
      Exception? cause = ended.Exception?.InnerException;
      foreach(TaskCompletionSource pending in _pending.Values)
      {
         pending.TrySetException(new RealtimeChannelClosedException("Realtime connection closed before acknowledgement; durable receipts will be replayed.", cause));
      }
      var both = Task.WhenAll(reader, writer);
      await both.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

      /////////////////////////////////////////////////////////////////////////
      // ended on purpose: disposed (whatever the loops met on a socket that //
      // was disposed under them is the dispose itself), or cancelled by the //
      // caller with no failure but the socket refusing a call as it ended.  //
      // anything else is rethrown: a failure, the reader's first, or a      //
      // cancellation nobody asked for                                       //
      /////////////////////////////////////////////////////////////////////////
      if(disposed)
      { return; }
      Exception? failure = FailureOf(reader, readerFailedFirst) ?? FailureOf(writer, writerFailedFirst);
      if(failure is not null)
      { ExceptionDispatchInfo.Throw(failure); }
      if(!token.IsCancellationRequested)
      { await (reader.IsCanceled ? reader : writer.IsCanceled ? writer : both).ConfigureAwait(false); }
   }



   /// <summary>
   /// What a loop failed with, or null when it did not fail or its failure is only the stop. A socket that has been
   /// aborted refuses a call in one of two ways: it is disposed, or it is in an invalid state. A loop that met that,
   /// and is not known to have failed before the stop, was stopped by the abort and not by anything the server or the
   /// network did. A connection the server dropped fails differently (closed prematurely) and is always reported.
   /// </summary>
   private static Exception? FailureOf(Task loop, bool failedBeforeStop)
   {
      Exception? failure = loop.Exception?.InnerException;
      bool refusedByAnEndedSocket = failure is ObjectDisposedException || (failure is WebSocketException { WebSocketErrorCode: WebSocketError.InvalidState });
      return failure is null || (!failedBeforeStop && refusedByAnEndedSocket) ? null : failure;
   }



   /// <summary>
   /// Queues one message. A frame over the negotiated size is refused before it is queued. For an acknowledged message the
   /// caller waits (up to the negotiated timeout) for the server's ACK, and the same message identifier cannot be in flight
   /// twice, so a replay after a lost acknowledgement is recognised by the server by its stable identity.
   /// </summary>
   public async Task SendAsync(string type, object payload, string? messageId = null, bool acknowledged = false, CancellationToken token = default)
   {
      token.ThrowIfCancellationRequested();
      if(Volatile.Read(ref _closed) != 0)
      { throw new RealtimeChannelClosedException("Realtime connection is closed.", null); }
      string id = messageId ?? Guid.NewGuid().ToString("N");
      byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { type, messageId = id, payload }, PrintApiClient.JsonOptions);
      if(bytes.Length > _limits.MaxFrameBytes)
      { throw new InvalidDataException("Realtime frame exceeds the negotiated limit."); }
      TaskCompletionSource? completion = acknowledged ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
      if(completion is not null && !_pending.TryAdd(id, completion))
      {
         throw new InvalidOperationException("The same durable message is already awaiting acknowledgement.");
      }
      try
      {
         Queue(bytes);
         if(completion is not null)
         {
            await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(_limits.AckTimeoutMillis), _time, token).ConfigureAwait(false);
         }
      }
      finally
      {
         if(completion is not null)
         { _pending.TryRemove(id, out _); }
      }
   }



   /// <summary>
   /// Adds a frame to the bounded outgoing queue, counting bytes and frames against the negotiated limits. Exceeding a limit
   /// aborts the socket and throws, which forces a reconnect, because dropping a frame silently would lose a durable event.
   /// A frame is also refused when the channel closed after the send was let in, while the frame was being serialized:
   /// the queue no longer takes anything then, and the send is told the channel is closed, which is what happened,
   /// instead of that the queue is full, which would be reported as the reason the connection dropped.
   /// </summary>
   private void Queue(byte[] bytes)
   {
      long queued = Interlocked.Add(ref _queuedBytes, bytes.Length);
      int frames = Interlocked.Increment(ref _queuedFrames);
      if(queued > _limits.MaxQueuedFrameBytes || frames > _limits.MaxQueuedFrames || !_outgoing.Writer.TryWrite(bytes))
      {
         Interlocked.Add(ref _queuedBytes, -bytes.Length);
         Interlocked.Decrement(ref _queuedFrames);
         if(Volatile.Read(ref _closed) != 0)
         { throw new RealtimeChannelClosedException("Realtime connection is closed.", null); }
         socket.Abort();
         throw new IOException("Realtime output queue is full; reconnect is required.");
      }
   }



   /// <summary>The single writer: sends queued frames one at a time, counts them as upload only after the socket accepted them, and always releases the queue accounting, even when a send fails.</summary>
   private async Task WriteAsync(CancellationToken token)
   {
      await foreach(byte[] bytes in _outgoing.Reader.ReadAllAsync(token).ConfigureAwait(false))
      {
         try
         {
            await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            traffic?.RecordUpload(bytes.Length);
         }
         finally { Interlocked.Add(ref _queuedBytes, -bytes.Length); Interlocked.Decrement(ref _queuedFrames); }
      }
   }



   /// <summary>
   /// The single reader. It assembles text messages from fragments within the negotiated size, rejects any other message
   /// type, has each complete message parsed and checked as an envelope, and routes it. A close frame ends the loop as a
   /// lost connection; anything the server gets wrong ends it as a <see cref="PrintingProtocolException"/>. Either way the
   /// connection closes and the runtime reconnects.
   /// </summary>
   private async Task ReadAsync(Action<RealtimeEnvelope> received, CancellationToken token)
   {
      byte[] buffer = new byte[8192];
      using var message = new MemoryStream();
      while(true)
      {
         token.ThrowIfCancellationRequested();
         ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false);
         traffic?.RecordDownload(result.Count);
         if(result.MessageType == WebSocketMessageType.Close)
         { throw new IOException("Server closed the realtime connection."); }
         if(result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > _limits.MaxFrameBytes)
         {
            throw new PrintingProtocolException("The realtime message is not text or exceeds the negotiated frame size.");
         }
         message.Write(buffer, 0, result.Count);
         if(!result.EndOfMessage)
         { continue; }
         DispatchFrame(RealtimeEnvelope.Parse(message.GetBuffer().AsMemory(0, checked((int)message.Length))), received);
         message.SetLength(0);
      }
   }



   /// <summary>
   /// Routes one validated frame. An ACK must name the message it acknowledges and completes the send waiting on it; an
   /// ACK for a message no longer waiting is ignored. An ERROR must carry a code; for a message still awaiting
   /// acknowledgement it fails that send with an HTTP-like status the callers already handle, and errors that mean
   /// authorization is gone are also passed on so the runtime can stop. Everything else goes to the callback.
   /// </summary>
   private void DispatchFrame(RealtimeEnvelope envelope, Action<RealtimeEnvelope> received)
   {
      if(string.Equals(envelope.Type, "ACK", StringComparison.Ordinal))
      {
         string acknowledged = ProtocolJson.RequiredText(envelope.Payload, "acknowledgedMessageId", 200, "realtime ACK frame");
         if(_pending.TryGetValue(acknowledged, out TaskCompletionSource? completion))
         { completion.TrySetResult(); }
         return;
      }
      if(string.Equals(envelope.Type, "ERROR", StringComparison.Ordinal))
      {
         string code = ProtocolJson.RequiredText(envelope.Payload, "code", 128, "realtime ERROR frame");
         if(_pending.TryGetValue(envelope.MessageId, out TaskCompletionSource? failed))
         {
            failed.TrySetException(new HttpRequestException("Realtime operation was rejected: " + code, null, RejectionStatus(code)));
            if(code is not ("AUTHORIZATION_REQUIRED" or "AUTHENTICATION_REQUIRED"))
            { return; }
         }
      }
      received(envelope);
   }



   /// <summary>Maps the server's rejection code to the HTTP status the same refusal would have had over HTTP, which is what the callers of an acknowledged send already handle.</summary>
   private static System.Net.HttpStatusCode RejectionStatus(string code) => code switch
   {
      "NOT_FOUND" => System.Net.HttpStatusCode.NotFound,
      "EXPIRED" => System.Net.HttpStatusCode.Gone,
      "CONFLICT" => System.Net.HttpStatusCode.Conflict,
      _ => System.Net.HttpStatusCode.BadRequest,
   };



   /// <summary>
   /// Marks the channel disposed and closed, stops the queue, fails any send still waiting so its caller replays the
   /// durable record, and disposes the socket. A run that is still going ends normally: disposing is its owner ending
   /// it on purpose. The waiting sends are told the channel is closed, the same failure a send gets when the channel
   /// closes for any other reason.
   /// </summary>
   public void Dispose()
   {
      Interlocked.Exchange(ref _disposed, 1);
      Interlocked.Exchange(ref _closed, 1);
      _outgoing.Writer.TryComplete();
      foreach(TaskCompletionSource pending in _pending.Values)
      { pending.TrySetException(new RealtimeChannelClosedException("Realtime connection was disposed.", null)); }
      socket.Dispose();
   }
}
