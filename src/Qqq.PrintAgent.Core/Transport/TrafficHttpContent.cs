using System.Net;

namespace Qqq.PrintAgent.Core.Transport;

internal sealed class TrafficHttpContent : HttpContent
{
   private readonly HttpContent _inner;
   private readonly Action<long> _record;

   internal TrafficHttpContent(HttpContent inner, Action<long> record)
   {
      _inner = inner;
      _record = record;
      foreach(KeyValuePair<string, IEnumerable<string>> header in inner.Headers)
      { Headers.TryAddWithoutValidation(header.Key, header.Value); }
   }

   protected override bool TryComputeLength(out long length)
   {
      length = _inner.Headers.ContentLength ?? 0;
      return _inner.Headers.ContentLength.HasValue;
   }

   protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
      => SerializeToStreamAsync(stream, context, CancellationToken.None);

   protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
   {
      using var counted = new TrafficStream(stream, _record, countReads: false, leaveOpen: true);
      await _inner.CopyToAsync(counted, context, cancellationToken).ConfigureAwait(false);
   }

   protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);
   protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
      => new TrafficStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _record, countReads: true);

   protected override void Dispose(bool disposing)
   {
      if(disposing)
      { _inner.Dispose(); }
      base.Dispose(disposing);
   }
}
