/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Qqq.PrintAgent.Core.Transport;

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>One continuously active reader and one bounded writer. Callbacks must only dispatch work, never await it.</summary>
public sealed class PrintingRealtimeChannel(WebSocket socket, RealtimeSettings limits, ApplicationTrafficMeter? traffic = null) : IDisposable
{
   private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
   private RealtimeSettings _limits = limits;
   private readonly ConcurrentDictionary<string, TaskCompletionSource> _pending = new(StringComparer.Ordinal);
   private long _queuedBytes;
   private int _queuedFrames;
   private int _closed;

   public bool IsOpen => Volatile.Read(ref _closed) == 0 && socket.State == WebSocketState.Open;

   public void UpdateSettings(RealtimeSettings settings)
   {
      settings.Validate();
      Volatile.Write(ref _limits, settings);
   }

   public async Task RunAsync(Action<RealtimeEnvelope> received, CancellationToken token)
   {
      using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
      Task reader = ReadAsync(received, lifetime.Token);
      Task writer = WriteAsync(lifetime.Token);
      try
      {
         await Task.WhenAny(reader, writer).ConfigureAwait(false);
      }
      finally
      {
         Interlocked.Exchange(ref _closed, 1);
         _outgoing.Writer.TryComplete();
         await lifetime.CancelAsync().ConfigureAwait(false);
         socket.Abort();
         foreach(TaskCompletionSource pending in _pending.Values)
         {
            pending.TrySetException(new IOException("Realtime connection closed before acknowledgement; durable receipts will be replayed."));
         }
         await Task.WhenAll(reader, writer).ConfigureAwait(false);
      }
   }

   public async Task SendAsync(string type, object payload, string? messageId = null, bool acknowledged = false, CancellationToken token = default)
   {
      token.ThrowIfCancellationRequested();
      if(Volatile.Read(ref _closed) != 0)
      { throw new IOException("Realtime connection is closed."); }
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
            await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(_limits.AckTimeoutMillis), token).ConfigureAwait(false);
         }
      }
      finally
      {
         if(completion is not null)
         { _pending.TryRemove(id, out _); }
      }
   }

   private void Queue(byte[] bytes)
   {
      long queued = Interlocked.Add(ref _queuedBytes, bytes.Length);
      int frames = Interlocked.Increment(ref _queuedFrames);
      if(queued > _limits.MaxQueuedFrameBytes || frames > _limits.MaxQueuedFrames || !_outgoing.Writer.TryWrite(bytes))
      {
         Interlocked.Add(ref _queuedBytes, -bytes.Length);
         Interlocked.Decrement(ref _queuedFrames);
         socket.Abort();
         throw new IOException("Realtime output queue is full; reconnect is required.");
      }
   }

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

   private async Task ReadAsync(Action<RealtimeEnvelope> received, CancellationToken token)
   {
      byte[] buffer = new byte[8192];
      using var message = new MemoryStream();
      while(!token.IsCancellationRequested)
      {
         ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false);
         traffic?.RecordDownload(result.Count);
         if(result.MessageType == WebSocketMessageType.Close)
         { throw new IOException("Server closed the realtime connection."); }
         if(result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > _limits.MaxFrameBytes)
         {
            throw new InvalidDataException("Realtime message type or size is invalid.");
         }
         message.Write(buffer, 0, result.Count);
         if(!result.EndOfMessage)
         { continue; }
         using var document = JsonDocument.Parse(message.GetBuffer().AsMemory(0, checked((int)message.Length)), new JsonDocumentOptions { MaxDepth = 24 });
         RejectDuplicateFields(document.RootElement);
         RealtimeEnvelope envelope = document.RootElement.Deserialize<RealtimeEnvelope>(PrintApiClient.JsonOptions) ?? throw new InvalidDataException("Missing realtime envelope.");
         if(string.IsNullOrEmpty(envelope.Type) || string.IsNullOrEmpty(envelope.MessageId) || envelope.MessageId.Length > 200)
         {
            throw new InvalidDataException("Invalid realtime envelope.");
         }
         DispatchFrame(envelope with { WireLength = checked((int)message.Length) }, received);
         message.SetLength(0);
      }
   }

   private static void RejectDuplicateFields(JsonElement value)
   {
      if(value.ValueKind == JsonValueKind.Object)
      {
         var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
         foreach(JsonProperty property in value.EnumerateObject())
         {
            if(!names.Add(property.Name))
            { throw new InvalidDataException("Duplicate realtime JSON field."); }
            RejectDuplicateFields(property.Value);
         }
      }
      else if(value.ValueKind == JsonValueKind.Array)
      {
         foreach(JsonElement child in value.EnumerateArray())
         { RejectDuplicateFields(child); }
      }
   }

   private void DispatchFrame(RealtimeEnvelope envelope, Action<RealtimeEnvelope> received)
   {
      if(string.Equals(envelope.Type, "ACK", StringComparison.Ordinal))
      {
         string? id = envelope.Payload.GetProperty("acknowledgedMessageId").GetString();
         if(id is not null && _pending.TryGetValue(id, out TaskCompletionSource? completion))
         { completion.TrySetResult(); }
         return;
      }
      if(string.Equals(envelope.Type, "ERROR", StringComparison.Ordinal) && _pending.TryGetValue(envelope.MessageId, out TaskCompletionSource? failed))
      {
         string? code = envelope.Payload.GetProperty("code").GetString();
         System.Net.HttpStatusCode status = code switch
         {
            "NOT_FOUND" => System.Net.HttpStatusCode.NotFound,
            "EXPIRED" => System.Net.HttpStatusCode.Gone,
            "CONFLICT" => System.Net.HttpStatusCode.Conflict,
            _ => System.Net.HttpStatusCode.BadRequest,
         };
         failed.TrySetException(new HttpRequestException("Realtime operation was rejected: " + code, null, status));
         if(code is not ("AUTHORIZATION_REQUIRED" or "AUTHENTICATION_REQUIRED"))
         { return; }
      }
      received(envelope with { Payload = envelope.Payload.Clone() });
   }

   public void Dispose()
   {
      Interlocked.Exchange(ref _closed, 1);
      _outgoing.Writer.TryComplete();
      foreach(TaskCompletionSource pending in _pending.Values)
      { pending.TrySetException(new IOException("Realtime connection was disposed.")); }
      socket.Dispose();
   }
}
