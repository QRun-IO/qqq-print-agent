/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;

namespace Qqq.PrintAgent.Core.Transport;

/// <summary>
/// Wraps an HTTP body so the bytes actually sent or received are counted, without buffering them or changing the headers or
/// length. It exists because the shared traffic display needs payload sizes from a handler that is otherwise opaque.
/// </summary>
internal sealed class TrafficHttpContent : HttpContent
{
   private readonly HttpContent _inner;
   private readonly Action<long> _record;



   /// <summary>Copies the wrapped content's headers onto this wrapper so the request or response looks unchanged to the caller, and keeps the counting callback.</summary>
   internal TrafficHttpContent(HttpContent inner, Action<long> record)
   {
      _inner = inner;
      _record = record;
      foreach(KeyValuePair<string, IEnumerable<string>> header in inner.Headers)
      { Headers.TryAddWithoutValidation(header.Key, header.Value); }
   }



   /// <summary>Reports the wrapped content's declared length, or says the length is unknown when it declares none, so chunked bodies stay chunked.</summary>
   protected override bool TryComputeLength(out long length)
   {
      length = _inner.Headers.ContentLength ?? 0;
      return _inner.Headers.ContentLength.HasValue;
   }



   /// <summary>Older serialization entry point, routed to the cancellable one so counting lives in a single place.</summary>
   protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
      => SerializeToStreamAsync(stream, context, CancellationToken.None);



   /// <summary>Copies the wrapped body to the network stream through a counting wrapper that leaves the network stream open, so each uploaded byte is counted exactly once.</summary>
   protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
   {
      using var counted = new TrafficStream(stream, _record, countReads: false, leaveOpen: true);
      await _inner.CopyToAsync(counted, context, cancellationToken).ConfigureAwait(false);
   }



   /// <summary>Older read-stream entry point, routed to the cancellable one.</summary>
   protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);



   /// <summary>Gives the reader a stream that counts bytes as they are read, so a streamed download is metered as it arrives rather than when it ends.</summary>
   protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
      => new TrafficStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _record, countReads: true);



   /// <summary>Disposes the wrapped content together with this wrapper, since the wrapper owns it.</summary>
   protected override void Dispose(bool disposing)
   {
      if(disposing)
      { _inner.Dispose(); }
      base.Dispose(disposing);
   }
}
