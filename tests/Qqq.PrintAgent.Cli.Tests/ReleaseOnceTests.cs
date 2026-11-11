/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>A lease gives back what it holds exactly once, however often and from however many threads it is disposed: a slot or a byte count returned twice would let more work in than the limit allows.</summary>
public sealed class ReleaseOnceTests
{
   /// <summary>Nothing is released until the lease is disposed; disposing it again and again releases once.</summary>
   [Fact]
   public async Task RepeatedDisposal_ReleasesOnce()
   {
      int released = 0;
      ReleaseOnce lease = new(() => released++);

      int before = released;
      await lease.DisposeAsync();
      await lease.DisposeAsync();
      await lease.DisposeAsync();

      Assert.Equal(0, before);
      Assert.Equal(1, released);
   }



   /// <summary>Leaving an <c>await using</c> block releases, and an explicit disposal on an error path before that does not make it two.</summary>
   [Fact]
   public async Task NormalPathAndErrorPath_TogetherReleaseOnce()
   {
      int released = 0;

      await using(ReleaseOnce lease = new(() => released++))
      {
         await lease.DisposeAsync();
      }

      Assert.Equal(1, released);
   }



   /// <summary>
   /// Thirty-two threads dispose the same lease at the same moment, a thousand leases in a row: each one releases exactly
   /// once. The threads are held at a barrier so that they really do arrive together.
   /// </summary>
   [Fact]
   public void ConcurrentDisposal_ReleasesOnce()
   {
      const int threads = 32;
      const int rounds = 1000;
      int[] released = new int[rounds];
      ReleaseOnce[] leases = Enumerable.Range(0, rounds).Select(round => new ReleaseOnce(() => Interlocked.Increment(ref released[round]))).ToArray();
      using Barrier barrier = new(threads);

      Thread[] workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
      {
         foreach(ReleaseOnce lease in leases)
         {
            barrier.SignalAndWait(TimeSpan.FromSeconds(30));
            lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
         }
      })).ToArray();
      foreach(Thread worker in workers)
      { worker.Start(); }
      bool finished = workers.All(worker => worker.Join(TimeSpan.FromSeconds(60)));

      Assert.True(finished);
      Assert.All(released, count => Assert.Equal(1, count));
   }



   /// <summary>A release that throws has still been taken: the failure reaches the first disposer, and a later disposal neither throws nor releases again.</summary>
   [Fact]
   public async Task ReleaseThatThrows_IsNotRunAgain()
   {
      int attempts = 0;
      ReleaseOnce lease = new(() =>
      {
         attempts++;
         throw new InvalidOperationException("The release failed.");
      });

      InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await lease.DisposeAsync());
      await lease.DisposeAsync();

      Assert.Equal("The release failed.", thrown.Message);
      Assert.Equal(1, attempts);
   }



   /// <summary>A lease without a release action is a programming error and is refused when it is made.</summary>
   [Fact]
   public void MissingRelease_IsRefused()
      => Assert.Throws<ArgumentNullException>(() => new ReleaseOnce(null!));
}
