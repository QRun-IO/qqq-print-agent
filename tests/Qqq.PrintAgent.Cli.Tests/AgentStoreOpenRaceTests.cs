/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Stores that several tasks open on one journal at the same moment, as the service does when one signal wakes the claim
/// lane, the synchronizer and the queue preview. Each store must work on a database connection of its own.
/// </summary>
public sealed class AgentStoreOpenRaceTests
{
   private const int Workers = 8;
   private const int Rounds = 400;
   private const int WriteEvery = 50;



   /// <summary>
   /// A store takes its database connection only while it holds the journal's lock. The journal is migrated first, which
   /// leaves it closed: the migration's connection is not pooled. This thread then holds the journal's lock and lets
   /// another thread construct a store on it. The constructor must wait for the lock, and while it waits nothing may have
   /// the journal file open, which the test shows by opening the file for itself alone. With the open outside the lock the
   /// store's connection has the file before the constructor could be seen waiting, and the test fails every time. (Before
   /// the journal carried a schema version this test used a journal that did not exist yet and looked for the file the open
   /// created; a store no longer creates a journal, so it looks for the open handle instead.) This is
   /// the property that keeps two stores from being handed one connection: Microsoft.Data.Sqlite 10.0.0 records a pooled
   /// connection's owner only after it has left the pool's lock, and another open of the same journal in that gap can be
   /// given the same connection. There is no event for "the constructor is now waiting", so the test waits until the thread
   /// is blocked, and then gives it a further fifth of a second in which a constructor that did not wait would have finished.
   /// </summary>
   [Fact]
   public void StoreOpensItsJournalOnlyWhileItHoldsTheJournalsLock()
   {
      string root = Path.Combine(Path.GetTempPath(), "qqq-open-under-lock-" + Guid.NewGuid().ToString("N"));
      string folder = Path.Combine(root, "journal");
      string file = Path.Combine(folder, "journal.db");
      try
      {
         // Loads the SQLite provider and the store's types, so the thread below has nothing else to wait for.
         using(JournalStore warm = TestState.OpenStore(Path.Combine(root, "warm")))
         { Assert.Null(warm.GetState("none")); }
         _ = TestState.Migrate(folder);
         Exception? failure = null;
         var opening = new Thread(() =>
         {
            try
            {
               using JournalStore store = TestState.OpenStoreAsFound(folder);
               _ = store.GetState("none");
            }
            catch(Exception exception) when(exception is SqliteException or IOException or InvalidOperationException)
            { failure = exception; }
         });
         bool reachedTheLock;
         bool finishedWhileHeld;
         bool openedWhileHeld;
         lock(TestState.SchemaLockFor(folder))
         {
            opening.Start();
            reachedTheLock = SpinWait.SpinUntil(() => opening.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(30));
            finishedWhileHeld = opening.Join(TimeSpan.FromMilliseconds(200));
            // A store that had opened would hold the file for as long as it waits here, so trying for a while costs a correct
            // run nothing and lets a virus scanner that looked at the new file for a moment get out of the way.
            openedWhileHeld = !SpinWait.SpinUntil(() => CanBeOpenedAlone(file), TimeSpan.FromSeconds(5));
         }
         opening.Join();
         Assert.True(reachedTheLock, "The constructor never came to wait for the journal's lock.");
         Assert.False(finishedWhileHeld, "The constructor finished while another thread held the journal's lock.");
         Assert.False(openedWhileHeld, "The store opened its journal before it held the journal's lock.");
         Assert.Null(failure);
         Assert.False(CanBeOpenedAlone(file), "The store that opened after the lock was released left no connection in the journal's pool, so the check above proved nothing.");
      }
      finally
      {
         JournalFiles.Delete(root);
      }
   }



   /// <summary>True when nothing has the file open: opening it with no sharing succeeds only then.</summary>
   private static bool CanBeOpenedAlone(string file)
   {
      try
      {
         using FileStream alone = new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
         return true;
      }
      catch(IOException)
      {
         return false;
      }
   }



   /// <summary>
   /// Eight threads open a store on one journal at the same instant, round after round, read from it inside a transaction
   /// and close it. Every fiftieth round a thread also records a job event (an explicit transaction) and saves a printer
   /// profile (a single statement) and reads both back; the writes take turns, so the threads do not wait out each other's
   /// write locks. Every round starts with the journal's connection pool empty, as it is after the idle minutes that prune
   /// it, so every open has to make a connection. No open and no statement may fail, and each thread must read back what it
   /// wrote, at once and at the end. With the open outside the journal's lock, two of the threads were now and then handed
   /// one connection and a statement failed with SQLite's "SQL logic error": in three runs of three at 4,000 rounds (two
   /// minutes each), about once in two thousand rounds, and in none of four runs at 1,000 rounds. At the 400 rounds kept
   /// here the loop is therefore a check that stores opened together work, not a dependable detector of that fault; the
   /// test above is what fails when the open moves back out of the lock.
   /// </summary>
   [Fact]
   public void StoresOpenedTogetherOnOneJournal_EachWorkOnTheirOwnConnection()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-open-race-" + Guid.NewGuid().ToString("N"));
      var failures = new ConcurrentQueue<string>();
      try
      {
         // Kept open so that closing the round's stores never closes the journal, which would checkpoint it every round.
         using JournalStore keeper = TestState.OpenStore(directory);
         using var writing = new SemaphoreSlim(1, 1);
         using var together = new Barrier(Workers, _ => JournalFiles.Release(directory));
         Thread[] threads = Enumerable.Range(0, Workers).Select(worker => new Thread(() => RunRounds(directory, worker, together, writing, failures))).ToArray();
         foreach(Thread thread in threads)
         { thread.Start(); }
         foreach(Thread thread in threads)
         { thread.Join(); }
         Assert.True(failures.IsEmpty, $"{failures.Count} failure(s): {string.Join(" | ", failures.Take(4))}");
         Assert.All(Enumerable.Range(0, Workers), worker =>
         {
            Assert.Equal(StateOf(LastWrite(worker)), keeper.GetState(JobOf(worker)));
            Assert.Equal(NameOf(LastWrite(worker)), keeper.GetPrinterProfile(PrinterOf(worker)).DisplayName);
         });
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>One thread's rounds: wait until all threads are ready, open a store, use it, close it. Whatever goes wrong is noted and the rounds go on, so the other threads are never left waiting.</summary>
   private static void RunRounds(string directory, int worker, Barrier together, SemaphoreSlim writing, ConcurrentQueue<string> failures)
   {
      for(int round = 0; round < Rounds; round++)
      {
         try
         {
            if(!together.SignalAndWait(TimeSpan.FromSeconds(30)))
            { throw new TimeoutException("The threads did not all become ready to open."); }
            if(!failures.IsEmpty)
            { continue; }
            using JournalStore store = TestState.OpenStore(directory);
            for(int query = 0; query < 4; query++)
            { _ = store.QueryJobs(new JobQuery()); }
            if(round % WriteEvery == worker)
            { WriteAndReadBack(store, worker, round, writing, failures); }
         }
         catch(Exception exception) when(exception is SqliteException or InvalidOperationException or ObjectDisposedException or IOException or TimeoutException or BarrierPostPhaseException or ArgumentException)
         {
            failures.Enqueue($"round {round}, thread {worker}: {exception.GetType().Name}: {exception.Message}");
         }
      }
   }



   /// <summary>Records an event and saves a profile through the store, then reads both back; one thread writes at a time.</summary>
   private static void WriteAndReadBack(JournalStore store, int worker, int round, SemaphoreSlim writing, ConcurrentQueue<string> failures)
   {
      writing.Wait();
      try
      {
         store.Record(JobOf(worker), new PrintEvent("event-" + worker.ToString(CultureInfo.InvariantCulture) + "-" + round.ToString(CultureInfo.InvariantCulture), StateOf(round)));
         store.SavePrinterProfile(PrinterOf(worker), new PrinterProfile(NameOf(round)));
         if(!string.Equals(store.GetState(JobOf(worker)), StateOf(round), StringComparison.Ordinal) || !string.Equals(store.GetPrinterProfile(PrinterOf(worker)).DisplayName, NameOf(round), StringComparison.Ordinal))
         { failures.Enqueue($"round {round}, thread {worker}: the journal does not hold what this thread just wrote."); }
      }
      finally { _ = writing.Release(); }
   }



   /// <summary>The last round in which a thread wrote.</summary>
   private static int LastWrite(int worker) => ((Rounds - 1 - worker) / WriteEvery * WriteEvery) + worker;



   /// <summary>The job a thread records events for.</summary>
   private static string JobOf(int worker) => "job-" + worker.ToString(CultureInfo.InvariantCulture);



   /// <summary>The printer whose profile a thread saves.</summary>
   private static string PrinterOf(int worker) => "printer-" + worker.ToString(CultureInfo.InvariantCulture);



   /// <summary>The state recorded in a round; it differs from one write to the next, so a write that was lost shows as the earlier state.</summary>
   private static string StateOf(int round) => round / WriteEvery % 2 == 0 ? "RECEIVED" : "SUBMITTING";



   /// <summary>The display name saved in a round.</summary>
   private static string NameOf(int round) => "Queue " + round.ToString(CultureInfo.InvariantCulture);
}
