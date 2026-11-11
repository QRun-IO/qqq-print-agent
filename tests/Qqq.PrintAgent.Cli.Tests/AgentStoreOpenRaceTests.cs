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
   /// A store takes its database connection only while it holds the journal's schema lock. This thread holds that lock for a
   /// journal that does not exist yet and lets another thread construct a store for it. The constructor creates the folder,
   /// then must wait for the lock; while it waits, the journal file must not exist, because opening the connection is what
   /// creates it. With the open outside the lock the file appears before the constructor waits, and the test fails every
   /// time. This is the property that keeps two stores from being handed one connection: Microsoft.Data.Sqlite 10.0.0 records
   /// a pooled connection's owner only after it has left the pool's lock, and another open of the same journal in that gap can
   /// be given the same connection. There is no event for "the constructor is now waiting", so the test waits until the
   /// folder exists and the thread is blocked, and then gives it a further fifth of a second in which a constructor that
   /// opened first would have created the file.
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
         Exception? failure = null;
         var opening = new Thread(() =>
         {
            try
            {
               using JournalStore store = TestState.OpenStore(folder);
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
            reachedTheLock = SpinWait.SpinUntil(() => Directory.Exists(folder) && opening.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(30));
            finishedWhileHeld = opening.Join(TimeSpan.FromMilliseconds(200));
            openedWhileHeld = File.Exists(file);
         }
         opening.Join();
         Assert.True(reachedTheLock, "The constructor never came to wait for the journal's lock.");
         Assert.False(finishedWhileHeld, "The constructor finished while another thread held the journal's lock.");
         Assert.False(openedWhileHeld, "The store opened its journal before it held the journal's lock.");
         Assert.Null(failure);
         Assert.True(File.Exists(file));
      }
      finally
      {
         JournalFiles.Delete(root);
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
