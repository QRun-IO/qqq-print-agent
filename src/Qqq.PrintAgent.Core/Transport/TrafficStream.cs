/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Transport;

/// <summary>Counts completed payload reads or writes without buffering or retaining their contents.</summary>
internal sealed class TrafficStream(Stream inner, Action<long> record, bool countReads, bool leaveOpen = false) : Stream
{
   /// <summary>Delegates capability and length queries to the wrapped stream, so wrapping never changes what the caller may do.</summary>
   public override bool CanRead => inner.CanRead;



   /// <summary>Whether the wrapped stream can seek.</summary>
   public override bool CanSeek => inner.CanSeek;



   /// <summary>Whether the wrapped stream can be written.</summary>
   public override bool CanWrite => inner.CanWrite;



   /// <summary>The wrapped stream's length.</summary>
   public override long Length => inner.Length;



   /// <summary>The wrapped stream's position; seeking is not counted as traffic.</summary>
   public override long Position { get => inner.Position; set => inner.Position = value; }



   /// <summary>Flushes the wrapped stream; flushing moves no new payload, so it is not counted.</summary>
   public override void Flush() => inner.Flush();



   /// <summary>Asynchronously flushes the wrapped stream without counting.</summary>
   public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);



   /// <summary>Repositions the wrapped stream.</summary>
   public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);



   /// <summary>Resizes the wrapped stream.</summary>
   public override void SetLength(long value) => inner.SetLength(value);



   /// <summary>Array read, routed to the span overload so each read is counted in exactly one place.</summary>
   public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));



   /// <summary>Reads from the wrapped stream and, when this wrapper counts reads, records the bytes actually received, so a short or empty read is counted truthfully.</summary>
   public override int Read(Span<byte> buffer)
   {
      int count = inner.Read(buffer);
      if(countReads)
      { record(count); }
      return count;
   }



   /// <summary>Array async read, routed to the memory overload so each read is counted in exactly one place.</summary>
   public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
      => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();



   /// <summary>Asynchronously reads from the wrapped stream and records the bytes received when this wrapper counts reads.</summary>
   public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
   {
      int count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
      if(countReads)
      { record(count); }
      return count;
   }



   /// <summary>Array write, routed to the span overload so each write is counted in exactly one place.</summary>
   public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));



   /// <summary>Writes to the wrapped stream and, when this wrapper counts writes, records the bytes only after the write succeeded, so a failed write is not counted as traffic.</summary>
   public override void Write(ReadOnlySpan<byte> buffer)
   {
      inner.Write(buffer);
      if(!countReads)
      { record(buffer.Length); }
   }



   /// <summary>Array async write, routed to the memory overload so each write is counted in exactly one place.</summary>
   public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
      => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();



   /// <summary>Asynchronously writes to the wrapped stream and records the bytes after the write completed when this wrapper counts writes.</summary>
   public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
   {
      await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
      if(!countReads)
      { record(buffer.Length); }
   }



   /// <summary>Disposes the wrapped stream unless the caller asked to keep it open, so wrapping a stream does not change who owns it.</summary>
   protected override void Dispose(bool disposing)
   {
      if(disposing && !leaveOpen)
      { inner.Dispose(); }
      base.Dispose(disposing);
   }
}
