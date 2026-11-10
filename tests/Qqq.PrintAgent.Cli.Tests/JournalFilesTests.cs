/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Tests of the cleanup the other tests rely on. Tests run side by side, each on a journal of its own, so cleaning up after
/// one test must never reach the journal of another.
/// </summary>
public sealed class JournalFilesTests
{
   /// <summary>
   /// One thread keeps releasing the files of one journal, as a finishing test does, while this thread opens and closes
   /// stores on another journal several thousand times. Every open must succeed. When the release cleared every pool in the
   /// process instead, an open here failed within a few thousand attempts with a disposed SQLite handle: the pool hands a
   /// connection out before marking whose it is, and a clear that runs in between takes it for abandoned and disposes it.
   /// This is a stress loop, so it shows the fault with high likelihood, not with certainty.
   /// </summary>
   [Fact]
   public void ReleasingOneJournalsFiles_DoesNotDisturbStoresOpeningAnother()
   {
      string root = Path.Combine(Path.GetTempPath(), "qqq-journal-files-" + Guid.NewGuid().ToString("N"));
      string released = Path.Combine(root, "released");
      string opened = Path.Combine(root, "opened");
      using(AgentStore seeded = new(released))
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
         for(int attempt = 0; attempt < 5000; attempt++)
         {
            using AgentStore store = new(opened);
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