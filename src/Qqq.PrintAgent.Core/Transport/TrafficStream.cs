/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Transport;

/// <summary>Counts completed payload reads or writes without buffering or retaining their contents.</summary>
internal sealed class TrafficStream(Stream inner, Action<long> record, bool countReads, bool leaveOpen = false) : Stream
{
   public override bool CanRead => inner.CanRead;
   public override bool CanSeek => inner.CanSeek;
   public override bool CanWrite => inner.CanWrite;
   public override long Length => inner.Length;
   public override long Position { get => inner.Position; set => inner.Position = value; }
   public override void Flush() => inner.Flush();
   public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
   public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
   public override void SetLength(long value) => inner.SetLength(value);

   public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
   public override int Read(Span<byte> buffer)
   {
      int count = inner.Read(buffer);
      if(countReads)
      { record(count); }
      return count;
   }
   public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
      => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
   public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
   {
      int count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
      if(countReads)
      { record(count); }
      return count;
   }

   public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
   public override void Write(ReadOnlySpan<byte> buffer)
   {
      inner.Write(buffer);
      if(!countReads)
      { record(buffer.Length); }
   }
   public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
      => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
   public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
   {
      await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
      if(!countReads)
      { record(buffer.Length); }
   }

   protected override void Dispose(bool disposing)
   {
      if(disposing && !leaveOpen)
      { inner.Dispose(); }
      base.Dispose(disposing);
   }
}
