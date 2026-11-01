/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

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
         using(AgentStore valid = new(Path.Combine(directory, "valid")))
         {
            Assert.Null(valid.GetState("none"));
         }
         string path = Path.Combine(directory, "journal.db");
         using(var database = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path }.ToString()))
         {
            database.Open();
            using Microsoft.Data.Sqlite.SqliteCommand command = database.CreateCommand();
            command.CommandText = "CREATE TABLE events(unexpected TEXT)";
            command.ExecuteNonQuery();
         }
         Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => new AgentStore(directory));
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
         Assert.True(exclusive.Length > 0);
      }
      finally
      {
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
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
         using(AgentStore store = new(directory))
         {
            store.TrackJob(new PrintJob("long-job", "printer", title, "PDF", "00", 1, new PrintOptions(), "", "CLAIMED"), printerName);
            store.Record("long-job", new PrintEvent("received", "RECEIVED"));
         }
         using AgentStore restored = new(directory);
         JobDetailsSnapshot details = Assert.IsType<JobDetailsSnapshot>(restored.GetJobDetails("long-job"));
         Assert.Equal(title, details.Job.Title);
         Assert.Equal(printerName, details.Job.PrinterName);
         Assert.Equal(title, Assert.Single(restored.QueryJobs(new JobQuery(Search: "ttt")).Jobs).Title);
      }
      finally
      {
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>The recent-jobs list holds 50 entries, but an old failure the operator has not reviewed stays in it even after 55 later successes, and drops out only once it has been acknowledged.</summary>
   [Fact]
   public void RecentJobs_KeepsOlderUnreviewedFailureAccessibleAfterFiftySuccesses()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-job-attention-" + Guid.NewGuid().ToString("N"));
      try
      {
         using AgentStore store = new(directory);
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
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
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
         using(AgentStore store = new(directory))
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
         using(AgentStore restored = new(directory))
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
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
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
         using AgentStore store = new(directory);
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
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
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
         using(AgentStore original = new(directory))
         {
            original.SaveConfiguration(new AgentConfiguration("https://example.com", "old-device", "old-token"));
            original.Record("old-job", new PrintEvent("old-event", "SUBMITTING"));
         }
         string path = Path.Combine(directory, "connection.dpapi");
         byte[] unreadable = [1, 2, 3, 4];
         File.WriteAllBytes(path, unreadable);
         Assert.Throws<System.Security.Cryptography.CryptographicException>(() => new AgentStore(directory));
         Assert.Equal(unreadable, File.ReadAllBytes(path));
         using(AgentStore recovery = new(directory, recoverUnreadableConfiguration: true))
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
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>A recorded SUBMITTING event and its undelivered receipt survive a restart, and the receipt stops being pending only after it is acknowledged; this is what lets the agent know a job may already have printed.</summary>
   [Fact]
   public void Journal_RetainsSubmissionAndPendingEventsAcrossRestart()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-print-test-" + Guid.NewGuid().ToString("N"));
      try
      {
         using(AgentStore store = new(directory))
         {
            store.Record("j1", new PrintEvent("e1", "SUBMITTING"));
         }
         using(AgentStore restored = new(directory))
         {
            Assert.Equal("SUBMITTING", restored.GetState("j1"));
            Assert.Single(restored.Pending());
            restored.Acknowledge("e1");
            Assert.Empty(restored.Pending());
         }
      }
      finally
      {
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>The saved server address, device id and token read back equal under the same Windows identity, while the device token does not appear as plain text in the file on disk.</summary>
   [Fact]
   public void Configuration_IsProtectedAndRoundTripsUnderCurrentIdentity()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-print-test-" + Guid.NewGuid().ToString("N"));
      try
      {
         using AgentStore store = new(directory);
         AgentConfiguration configuration = new("https://example.com", "test-device", "not-a-real-secret");
         store.SaveConfiguration(configuration);
         Assert.Equal(configuration, store.LoadConfiguration());
         byte[] file = File.ReadAllBytes(Path.Combine(directory, "connection.dpapi"));
         Assert.DoesNotContain("not-a-real-secret", System.Text.Encoding.UTF8.GetString(file), StringComparison.Ordinal);
      }
      finally
      {
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
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
         using AgentStore oldWorker = new(directory);
         oldWorker.SaveConfiguration(original);
         oldWorker.Record("j1", new PrintEvent("e1", "SUBMITTING"));
         using(AgentStore management = new(directory))
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
         using(AgentStore newWorker = new(directory))
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
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
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
         using(AgentStore initial = new(directory))
         {
            initial.SaveConfiguration(original);
            initial.Record("legacy", new PrintEvent("legacy-event", "RECEIVED"));
            initial.Acknowledge("legacy-event");
         }
         using(var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(directory, "journal.db")}"))
         {
            connection.Open();
            using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP INDEX IF EXISTS events_outbox; DROP INDEX IF EXISTS events_job; DROP TABLE local_jobs; ALTER TABLE events DROP COLUMN connection_id";
            command.ExecuteNonQuery();
         }
         using(AgentStore migrated = new(directory))
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
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
      }
   }

}
