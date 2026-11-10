/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The agent store is the local durable record of what was claimed, attempted and reported. These tests protect what must not
/// be lost or confused: pending receipts, uncertain submissions, operator review state, retention limits, protected
/// credentials, and separation of one enrollment's history from another's.
/// </summary>
public sealed class AgentStoreTests
{
   /// <summary>A database with an unexpected schema makes the constructor fail, and the failure must release the file so the damaged journal can be inspected or replaced rather than left locked by a half-built store.</summary>
   [Fact]
   public void InvalidSchema_DoesNotKeepDatabaseOpenAfterConstructorFailure()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-invalid-schema-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(directory);
      try
      {
         // Initialize the Windows SQLite provider through a separate valid store.
         using(AgentStore valid = TestState.OpenStore(Path.Combine(directory, "valid")))
         {
            Assert.Null(valid.GetState("none"));
         }
         string path = Path.Combine(directory, "journal.db");
         using(Microsoft.Data.Sqlite.SqliteConnection database = TestState.OpenJournal(directory))
         {
            using Microsoft.Data.Sqlite.SqliteCommand command = database.CreateCommand();
            command.CommandText = "CREATE TABLE events(unexpected TEXT)";
            command.ExecuteNonQuery();
         }
         Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => TestState.OpenStore(directory));
         JournalFiles.Release(directory);
         using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
         Assert.True(exclusive.Length > 0);
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>
   /// Many stores opened at once on one new journal all succeed and each can write its profile: schema creation and migration
   /// are serialized for that journal, whichever store gets there first. Each store opens on a thread of its own, and a
   /// barrier lets them go only when all sixteen are ready, so the opens really do overlap whatever the thread pool is doing.
   /// The directory is addressed in three spellings (as given, in upper case, and with a trailing separator), because Windows
   /// treats them as the same journal and so must the lock.
   /// </summary>
   [Fact]
   public void ConcurrentOpensOfOneNewJournal_AllSucceedAndShareItsSchema()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-concurrent-open-" + Guid.NewGuid().ToString("N"));
      string[] spellings = [directory, directory.ToUpperInvariant(), directory + Path.DirectorySeparatorChar];
      // Each spelling is a connection string of its own, so each has a pool of its own to release before the folder can go.
      int stores = 16;
      try
      {
         using var together = new Barrier(stores);
         var failures = new ConcurrentQueue<Exception>();
         Thread[] opens = Enumerable.Range(0, stores).Select(index => new Thread(() =>
         {
            try
            {
               if(!together.SignalAndWait(TimeSpan.FromSeconds(30)))
               { throw new TimeoutException("The stores did not all become ready to open."); }
               using AgentStore store = TestState.OpenStore(spellings[index % spellings.Length]);
               store.SavePrinterProfile("printer-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), new PrinterProfile("Queue " + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            catch(Exception exception) when(exception is SqliteException or IOException or InvalidOperationException or TimeoutException or BarrierPostPhaseException)
            { failures.Enqueue(exception); }
         })).ToArray();
         foreach(Thread open in opens)
         { open.Start(); }
         foreach(Thread open in opens)
         { open.Join(); }
         Assert.Empty(failures);
         using AgentStore reopened = TestState.OpenStore(directory);
         Assert.All(Enumerable.Range(0, stores), index => Assert.Equal("Queue " + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reopened.GetPrinterProfile("printer-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)).DisplayName));
      }
      finally
      {
         JournalFiles.Delete(directory, spellings);
      }
   }



   /// <summary>
   /// Stores on different folders do not wait for each other. This thread holds the schema lock of one journal, which every
   /// store opening that journal must take, and meanwhile a store on a second folder is opened and used on another thread.
   /// It must finish while the lock is still held. With one lock shared by all journals it cannot, and the test fails when
   /// its wait of 30 seconds runs out; nothing else in the test depends on time. The lock is the same one for the first
   /// folder written in upper case or with a trailing separator, and a different one for the second folder.
   /// </summary>
   [Fact]
   public void StoresOnDifferentDirectories_DoNotWaitForEachOther()
   {
      string root = Path.Combine(Path.GetTempPath(), "qqq-separate-journals-" + Guid.NewGuid().ToString("N"));
      string held = Path.Combine(root, "held");
      string free = Path.Combine(root, "free");
      try
      {
         using(AgentStore first = TestState.OpenStore(held))
         { Assert.Null(first.GetState("none")); }
         Exception? failure = null;
         using var finished = new ManualResetEventSlim();
         var opening = new Thread(() =>
         {
            try
            {
               using AgentStore other = TestState.OpenStore(free);
               other.SavePrinterProfile("printer", new PrinterProfile("Free queue"));
            }
            catch(Exception exception) when(exception is SqliteException or IOException or InvalidOperationException)
            { failure = exception; }
            finally { finished.Set(); }
         });
         Lock schemaLock = TestState.SchemaLockFor(held);
         bool finishedWhileHeld;
         lock(schemaLock)
         {
            opening.Start();
            finishedWhileHeld = finished.Wait(TimeSpan.FromSeconds(30));
         }
         opening.Join();
         Assert.True(finishedWhileHeld, "A store on another folder waited for this journal's schema lock.");
         Assert.Null(failure);
         // Compared as locks, not as objects: a lock passed as an object would lose its meaning to the compiler.
         Assert.True(schemaLock == TestState.SchemaLockFor(held.ToUpperInvariant()));
         Assert.True(schemaLock == TestState.SchemaLockFor(held + Path.DirectorySeparatorChar));
         Assert.True(schemaLock != TestState.SchemaLockFor(free));
         using AgentStore reopened = TestState.OpenStore(free);
         Assert.Equal("Free queue", reopened.GetPrinterProfile("printer").DisplayName);
      }
      finally
      {
         JournalFiles.Delete(root);
      }
   }



   /// <summary>Document and printer names at the protocol maximum of 1,024 characters are stored whole, survive a restart, and remain searchable by prefix, so long names are never silently truncated in the history.</summary>
   [Fact]
   public void JobDetails_PreserveFullProtocolLengthNamesAcrossRestart()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-long-job-" + Guid.NewGuid().ToString("N"));
      string title = new string('t', 1024);
      string printerName = new string('p', 1024);
      try
      {
         using(AgentStore store = TestState.OpenStore(directory))
         {
            store.TrackJob(new PrintJob("long-job", "printer", title, "PDF", "00", 1, new PrintOptions(), "", "CLAIMED"), printerName);
            store.Record("long-job", new PrintEvent("received", "RECEIVED"));
         }
         using AgentStore restored = TestState.OpenStore(directory);
         JobDetailsSnapshot details = Assert.IsType<JobDetailsSnapshot>(restored.GetJobDetails("long-job"));
         Assert.Equal(title, details.Job.Title);
         Assert.Equal(printerName, details.Job.PrinterName);
         Assert.Equal(title, Assert.Single(restored.QueryJobs(new JobQuery(Search: "ttt")).Jobs).Title);
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>The recent-jobs list holds 50 entries, but an old failure the operator has not reviewed stays in it even after 55 later successes, and drops out only once it has been acknowledged.</summary>
   [Fact]
   public void RecentJobs_KeepsOlderUnreviewedFailureAccessibleAfterFiftySuccesses()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-job-attention-" + Guid.NewGuid().ToString("N"));
      try
      {
         using AgentStore store = TestState.OpenStore(directory);
         store.Record("old-failure", new PrintEvent("old-event", "OUTCOME_UNKNOWN"));
         for(int index = 0; index < 55; index++)
         {
            string id = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            store.Record(id, new PrintEvent(id, "SPOOLED"));
         }
         Assert.Equal(50, store.RecentJobs().Count);
         Assert.Equal("old-failure", store.RecentJobs()[0].JobId);
         Assert.Equal(1, store.NeedsAttentionCount());
         store.AcknowledgeFailure("old-failure");
         Assert.Equal(0, store.NeedsAttentionCount());
         Assert.DoesNotContain(store.RecentJobs(), job => string.Equals(job.JobId, "old-failure", StringComparison.Ordinal));
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>
   /// Job details keep the document, printer and format across a restart. An operator marking a failure as reviewed is a separate
   /// fact from the server having received the receipt: reviewing never marks the failure event as delivered.
   /// </summary>
   [Fact]
   public void JobDetails_PersistIdentityAndIndependentOperatorAcknowledgement()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-job-details-" + Guid.NewGuid().ToString("N"));
      try
      {
         using(AgentStore store = TestState.OpenStore(directory))
         {
            PrintJob job = new("job", "printer", "Packing slip", "PDF", "00", 1, new PrintOptions(), "", "CLAIMED");
            store.TrackJob(job, "Warehouse Laser");
            store.Record("job", new PrintEvent("received", "RECEIVED"));
            store.Acknowledge("received");
            store.Record("job", new PrintEvent("failure", "FAILED", "Paper unavailable."));
            Assert.Equal(1, store.NeedsAttentionCount());
            store.AcknowledgeFailure("job");
            Assert.Equal(0, store.NeedsAttentionCount());
            Assert.Single(store.Pending()); // Operator review cannot acknowledge server delivery.
         }
         using(AgentStore restored = TestState.OpenStore(directory))
         {
            JobDetailsSnapshot details = Assert.IsType<JobDetailsSnapshot>(restored.GetJobDetails("job"));
            Assert.Equal("Packing slip", details.Job.Title);
            Assert.Equal("Warehouse Laser", details.Job.PrinterName);
            Assert.Equal("printer", details.Job.PrinterId);
            Assert.Equal("PDF", details.Job.Format);
            Assert.True(details.Job.FailureAcknowledged);
            Assert.Collection(details.Events, item => Assert.True(item.Delivered), item => Assert.False(item.Delivered));
            Assert.Null(restored.GetJobDetails("another-job"));
            Assert.Throws<InvalidOperationException>(() => restored.AcknowledgeFailure("another-job"));
         }
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>
   /// Pruning detailed history trims only a completed, delivered job's event list (its final state and spooler id remain, so a
   /// redelivered job is still recognized and not printed twice). Jobs with an undelivered receipt, an unreviewed uncertain
   /// outcome or active work keep their events until each of those conditions is cleared.
   /// </summary>
   [Fact]
   public void Retention_PreservesUndeliveredUnreviewedAndActiveJobsAndDeduplication()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-job-retention-" + Guid.NewGuid().ToString("N"));
      try
      {
         using AgentStore store = TestState.OpenStore(directory);
         store.Record("completed", new PrintEvent("completed-event", "SPOOLED", SpoolerJobId: "123"));
         store.Acknowledge("completed-event");
         store.Record("unreviewed", new PrintEvent("unreviewed-event", "OUTCOME_UNKNOWN"));
         store.Acknowledge("unreviewed-event");
         store.Record("undelivered", new PrintEvent("undelivered-event", "FAILED"));
         store.AcknowledgeFailure("undelivered");
         store.Record("active", new PrintEvent("active-event", "SUBMITTING"));
         store.Acknowledge("active-event");
         Assert.Equal(1, store.PruneHistory(maxDetailedJobs: 1));
         Assert.Equal("SPOOLED", store.GetState("completed"));
         JobDetailsSnapshot retained = Assert.IsType<JobDetailsSnapshot>(store.GetJobDetails("completed"));
         Assert.True(retained.HistoryPruned);
         Assert.Empty(retained.Events);
         Assert.Equal("123", retained.Job.SpoolerJobId);
         Assert.Single(store.GetJobDetails("unreviewed")!.Events);
         Assert.Single(store.GetJobDetails("undelivered")!.Events);
         Assert.Single(store.GetJobDetails("active")!.Events);
         Assert.Equal(1, store.NeedsAttentionCount());
         Assert.Equal(0, store.PruneHistory(maxDetailedJobs: 1));
         store.AcknowledgeFailure("unreviewed");
         store.Acknowledge("undelivered-event");
         Assert.Equal(1, store.PruneHistory(maxDetailedJobs: 1));
         Assert.Single(store.GetJobDetails("unreviewed")!.Events);
         Assert.Equal("OUTCOME_UNKNOWN", store.GetState("unreviewed"));
         Assert.Equal("FAILED", store.GetState("undelivered"));
         Assert.Single(store.Incomplete());
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>
   /// Credentials that cannot be decrypted are never overwritten silently: a normal open fails and leaves the file untouched.
   /// An explicit recovery open starts a clean scope, keeps the unreadable file as a recovery copy when new credentials are
   /// saved, and brings the old pending and incomplete work back if the original device is enrolled again.
   /// </summary>
   [Fact]
   public void ExplicitReenrollment_PreservesUnreadableCredentialsAndOldJournal()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-print-recovery-" + Guid.NewGuid().ToString("N"));
      try
      {
         using(AgentStore original = TestState.OpenStore(directory))
         {
            original.SaveConfiguration(new AgentConfiguration("https://example.com", "old-device", "old-token"));
            original.Record("old-job", new PrintEvent("old-event", "SUBMITTING"));
         }
         string path = Path.Combine(directory, "connection.dpapi");
         byte[] unreadable = [1, 2, 3, 4];
         File.WriteAllBytes(path, unreadable);
         Assert.Throws<System.Security.Cryptography.CryptographicException>(() => TestState.OpenStore(directory));
         Assert.Equal(unreadable, File.ReadAllBytes(path));
         using(AgentStore recovery = TestState.OpenStore(directory, recoverUnreadableConfiguration: true))
         {
            Assert.Empty(recovery.Pending());
            Assert.Empty(recovery.Incomplete());
            Assert.Empty(Directory.GetFiles(directory, "connection.recovery-*.dpapi"));
            recovery.SaveConfiguration(new AgentConfiguration("https://example.com", "new-device", "new-token"));
            Assert.Equal(unreadable, File.ReadAllBytes(Assert.Single(Directory.GetFiles(directory, "connection.recovery-*.dpapi"))));
            Assert.Empty(recovery.Pending());
            Assert.Empty(recovery.Incomplete());
            Assert.Equal("new-device", recovery.LoadConfiguration()!.DeviceId);
            recovery.SaveConfiguration(new AgentConfiguration("https://example.com", "old-device", "rotated-token"));
            Assert.Equal("old-job", Assert.Single(recovery.Pending()).JobId);
            Assert.Equal("SUBMITTING", Assert.Single(recovery.Incomplete()).State);
         }
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>A recorded SUBMITTING event and its undelivered receipt survive a restart, and the receipt stops being pending only after it is acknowledged; this is what lets the agent know a job may already have printed.</summary>
   [Fact]
   public void Journal_RetainsSubmissionAndPendingEventsAcrossRestart()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-print-test-" + Guid.NewGuid().ToString("N"));
      try
      {
         using(AgentStore store = TestState.OpenStore(directory))
         {
            store.Record("j1", new PrintEvent("e1", "SUBMITTING"));
         }
         using(AgentStore restored = TestState.OpenStore(directory))
         {
            Assert.Equal("SUBMITTING", restored.GetState("j1"));
            Assert.Single(restored.Pending());
            restored.Acknowledge("e1");
            Assert.Empty(restored.Pending());
         }
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>The saved server address, device id and token read back equal under the same Windows identity, while the device token does not appear as plain text in the file on disk.</summary>
   [Fact]
   public void Configuration_IsProtectedAndRoundTripsUnderCurrentIdentity()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-print-test-" + Guid.NewGuid().ToString("N"));
      try
      {
         using AgentStore store = TestState.OpenStore(directory);
         AgentConfiguration configuration = new("https://example.com", "test-device", "not-a-real-secret");
         store.SaveConfiguration(configuration);
         Assert.Equal(configuration, store.LoadConfiguration());
         byte[] file = File.ReadAllBytes(Path.Combine(directory, "connection.dpapi"));
         Assert.DoesNotContain("not-a-real-secret", System.Text.Encoding.UTF8.GetString(file), StringComparison.Ordinal);
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>
   /// Enrolling against a different server and device starts a separate history, so the new connection cannot see or resend the
   /// old one's pending receipts. A worker still running under the old enrollment keeps its original scope, and returning to the
   /// original device (even with a rotated token or a trailing-slash address) restores its history in the order it happened.
   /// </summary>
   [Fact]
   public void Reenrollment_IsolatesPendingEventsAndPreservesPreviousConnectionHistory()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-print-scope-" + Guid.NewGuid().ToString("N"));
      try
      {
         AgentConfiguration original = new("https://example.com", "device-a", "old-token");
         AgentConfiguration replacement = new("https://other.example.com", "device-b", "new-token");
         using AgentStore oldWorker = TestState.OpenStore(directory);
         oldWorker.SaveConfiguration(original);
         oldWorker.Record("j1", new PrintEvent("e1", "SUBMITTING"));
         using(AgentStore management = TestState.OpenStore(directory))
         {
            management.SaveConfiguration(replacement);
            Assert.Empty(management.Pending());
            Assert.Empty(management.Incomplete());
            Assert.Empty(management.RecentJobs());
            Assert.Null(management.GetState("j1"));
         }
         // An in-flight old worker retains its original scope rather than the new credentials.
         Assert.Single(oldWorker.Pending());
         oldWorker.Record("j1", new PrintEvent("e2", "OUTCOME_UNKNOWN"));
         using(AgentStore newWorker = TestState.OpenStore(directory))
         {
            Assert.Equal(replacement, newWorker.LoadConfiguration());
            Assert.Empty(newWorker.Pending());
            newWorker.Record("j1", new PrintEvent("new-e1", "RECEIVED"));
            Assert.Equal("RECEIVED", newWorker.GetState("j1"));
            newWorker.SaveConfiguration(original with { DeviceToken = "rotated-token", ServerUrl = "https://example.com/" });
            Assert.Equal("OUTCOME_UNKNOWN", newWorker.GetState("j1"));
            Assert.Equal(2, newWorker.Pending().Count);
            Assert.Equal("e1", newWorker.Pending()[0].Event.EventId);
         }
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>
   /// A journal written before connection scoping existed is adopted by the connection that was saved at the time, so its
   /// incomplete job is still visible to that device. Enrolling elsewhere hides it, and re-enrolling the original device brings it back.
   /// </summary>
   [Fact]
   public void LegacyJournal_IsBoundToSavedConnectionBeforeReenrollment()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-print-migration-" + Guid.NewGuid().ToString("N"));
      try
      {
         AgentConfiguration original = new("https://example.com", "device-a", "old-token");
         using(AgentStore initial = TestState.OpenStore(directory))
         {
            initial.SaveConfiguration(original);
            initial.Record("legacy", new PrintEvent("legacy-event", "RECEIVED"));
            initial.Acknowledge("legacy-event");
         }
         using(Microsoft.Data.Sqlite.SqliteConnection connection = TestState.OpenJournal(directory))
         {
            using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP INDEX IF EXISTS events_outbox; DROP INDEX IF EXISTS events_job; DROP TABLE local_jobs; ALTER TABLE events DROP COLUMN connection_id";
            command.ExecuteNonQuery();
         }
         using(AgentStore migrated = TestState.OpenStore(directory))
         {
            Assert.Equal("RECEIVED", Assert.Single(migrated.Incomplete()).State);
            Assert.Empty(migrated.Pending());
            migrated.SaveConfiguration(new AgentConfiguration("https://other.example.com", "device-b", "new-token"));
            Assert.Empty(migrated.Incomplete());
            migrated.SaveConfiguration(original);
            Assert.Equal("legacy", Assert.Single(migrated.RecentJobs()).JobId);
            Assert.Equal("RECEIVED", Assert.Single(migrated.Incomplete()).State);
         }
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }

}
