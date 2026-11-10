/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Threading.Channels;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// A response body the test feeds by hand: each read waits until the test supplies the next chunk or ends the body, and
/// the test can wait for the reader to arrive at its next read. It stands in for a slow or stalled server without any
/// network or timing.
/// </summary>
internal sealed class FeedStream : Stream
{
   private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
   private readonly Channel<int> _reads = Channel.CreateUnbounded<int>();
   private int _readCount;



   /// <summary>How many reads the consumer has started.</summary>
   internal int Reads => Volatile.Read(ref _readCount);



   /// <inheritdoc/>
   public override bool CanRead => true;



   /// <inheritdoc/>
   public override bool CanSeek => false;



   /// <inheritdoc/>
   public override bool CanWrite => false;



   /// <inheritdoc/>
   public override long Length => throw new NotSupportedException();



   /// <inheritdoc/>
   public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }



   /// <summary>Supplies the bytes the consumer's pending or next read returns.</summary>
   internal void Feed(byte[] chunk) => Assert.True(_chunks.Writer.TryWrite(chunk));



   /// <summary>Ends the body: the consumer's pending or next read returns zero.</summary>
   internal void End() => _chunks.Writer.TryComplete();



   /// <summary>Completes when the consumer has started its next read; the ten second bound only stops a broken test from hanging.</summary>
   internal async Task NextReadAsync()
   {
      using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
      await _reads.Reader.ReadAsync(limit.Token);
   }



   /// <summary>Announces the read, then waits for the test to feed a chunk, end the body or the consumer to cancel.</summary>
   public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
   {
      Assert.True(_reads.Writer.TryWrite(Interlocked.Increment(ref _readCount)));
      if(!await _chunks.Reader.WaitToReadAsync(cancellationToken))
      { return 0; }
      Assert.True(_chunks.Reader.TryRead(out byte[]? chunk));
      Assert.True(chunk.Length <= buffer.Length, "Feed chunks no larger than the consumer's buffer.");
      chunk.CopyTo(buffer);
      return chunk.Length;
   }



   /// <inheritdoc/>
   public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();



   /// <inheritdoc/>
   public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("The download must read asynchronously.");



   /// <inheritdoc/>
   public override void Flush() => throw new NotSupportedException();



   /// <inheritdoc/>
   public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();



   /// <inheritdoc/>
   public override void SetLength(long value) => throw new NotSupportedException();



   /// <inheritdoc/>
   public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
