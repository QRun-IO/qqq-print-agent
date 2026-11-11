/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */


namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Tests of the cleanup the other tests rely on. Tests run side by side, each on a journal of its own, so cleaning up after
/// one test must never reach the journal of another.
/// </summary>
public sealed class JournalFilesTests
{
   /// <summary>
   /// One thread keeps releasing the files of one journal, as a finishing test does, while this thread opens and closes
   /// stores on another journal 2,500 times. Every open must succeed. When the release cleared every pool in the
   /// process instead, an open here failed within a few thousand attempts with a disposed SQLite handle: the pool hands a
   /// connection out before marking whose it is, and a clear that runs in between takes it for abandoned and disposes it.
   /// This is a stress loop, so it shows the fault with high likelihood, not with certainty. With the release clearing
   /// every pool, 2,500 opens failed in five runs of six and 1,500 in two of six, so 2,500 is as short as the loop can be
   /// and still show it; a thread that releases without pause slows the stores of the tests running beside it, which is
   /// why it is no longer. The releases are not paced by the opens, because paced releases never fell into the gap.
   /// </summary>
   [Fact]
   public void ReleasingOneJournalsFiles_DoesNotDisturbStoresOpeningAnother()
   {
      string root = Path.Combine(Path.GetTempPath(), "qqq-journal-files-" + Guid.NewGuid().ToString("N"));
      string released = Path.Combine(root, "released");
      string opened = Path.Combine(root, "opened");
      using(JournalStore seeded = TestState.OpenStore(released))
      { Assert.Null(seeded.GetState("none")); }
      using var stop = new CancellationTokenSource();
      var releasing = new Thread(() =>
      {
         while(!stop.IsCancellationRequested)
         { JournalFiles.Release(released); }
      });
      releasing.Start();
      try
      {
         for(int attempt = 0; attempt < 2500; attempt++)
         {
            using JournalStore store = TestState.OpenStore(opened);
            Assert.Null(store.GetState("none"));
         }
      }
      finally
      {
         stop.Cancel();
         releasing.Join();
         JournalFiles.Delete(root);
      }
   }
}
