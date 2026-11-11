/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The journal's schema version and the migration that sets it. The service migrates once, when it starts: every pending
/// step in one transaction, or none of them. These tests run that migration on the journals earlier releases left (built
/// by <see cref="LegacyStateBuilder"/> from the scripts in <c>Fixtures</c>), and show what it keeps, what it adds, what a
/// failing step leaves behind, and that a journal from a newer build is not touched in any way.
/// </summary>
public sealed class JournalMigrationTests
{
   /// <summary>The pairing whose scope most rows of the 1.2.1 fixture carry.</summary>
   private static readonly AgentConfiguration CurrentPairing = new("https://synthetic.invalid", "device", "synthetic-token");



   /// <summary>A folder that does not exist yet gets a journal at the current version with every table, and a store then opens on it.</summary>
   [Fact]
   public void Migrate_EmptyFolder_Version1()
   {
      string directory = NewFolder();
      try
      {
         Assert.False(Directory.Exists(directory));
         Assert.IsType<StateStatus.Ready>(TestState.Migrate(directory));
         Assert.Equal(1, TestState.SchemaVersion);
         Assert.Equal(1, JournalProbe.Version(directory));
         Assert.Equal(["control_commands", "control_state", "events", "exposed_printers", "local_jobs", "local_metadata", "realtime_settings", "server_trust", "sqlite_sequence"],
            JournalProbe.Tables(directory));
         Assert.Equal("wal", JournalProbe.Scalar(directory, "PRAGMA journal_mode"));
         using JournalStore store = TestState.OpenStoreAsFound(directory);
         Assert.Empty(store.RecentJobs());
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>
   /// A 1.2.1 journal already has everything step 1 would add, so migrating it changes one thing: the version. Every
   /// table's rows are the same before and after (a digest of each is compared), and so is every table and index. The
   /// current store then reads those rows as 1.2.1 wrote them: the jobs of the current pairing with their states, the two
   /// events not yet delivered, the sharing, the names, the control state with a member this version no longer has, the
   /// undelivered receipt, the cached policy and the certificate exception. The earlier pairing's job stays out of sight.
   /// </summary>
   [Fact]
   public void Migrate_Journal121_KeepsEveryRow()
   {
      string directory = NewFolder();
      try
      {
         _ = new LegacyStateBuilder(directory).WithJournal(LegacyStateBuilder.Journal121).WithPairing(CurrentPairing);
         IReadOnlyDictionary<string, string> rowsBefore = JournalProbe.RowDigests(directory);
         IReadOnlyList<string> schemaBefore = JournalProbe.Schema(directory);
         Assert.Equal(0, JournalProbe.Version(directory));
         Assert.Equal(9, rowsBefore.Count);
         Assert.DoesNotContain(rowsBefore, table => table.Value.StartsWith("0 rows", StringComparison.Ordinal));

         Assert.IsType<StateStatus.Ready>(TestState.Migrate(directory));

         Assert.Equal(1, JournalProbe.Version(directory));
         Assert.Equal(rowsBefore, JournalProbe.RowDigests(directory));
         Assert.Equal(schemaBefore, JournalProbe.Schema(directory));
         using JournalStore store = TestState.OpenStoreAsFound(directory);
         AssertReads121(store);
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>What the current store must read from the migrated 1.2.1 fixture; each line names a row of the script.</summary>
   private static void AssertReads121(JournalStore store)
   {
      Assert.Equal(["job-0950", "job-1001", "job-1002", "job-1003", "job-1004"], store.RecentJobs().Select(job => job.JobId).Order(StringComparer.Ordinal), StringComparer.Ordinal);
      Assert.Equal("SPOOLED", store.GetState("job-1001"));
      Assert.Null(store.GetState("job-0900"));
      Assert.Equal(["ev-1002-failed", "ev-1004-unknown"], store.Pending().Select(pending => pending.Event.EventId), StringComparer.Ordinal);
      Assert.Equal(2, store.NeedsAttentionCount());
      JobDetailsSnapshot details = store.GetJobDetails("job-1001")!;
      Assert.Equal(["RECEIVED", "SUBMITTING", "SPOOLED"], details.Events.Select(item => item.Status), StringComparer.Ordinal);
      Assert.Equal("41", details.Job.SpoolerJobId);
      Assert.Equal("2026-11-06T14:03:19.0000000+00:00", details.Job.CreatedAt);
      Assert.True(store.GetJobDetails("job-0950")!.HistoryPruned);
      Assert.Equal(["printer-a", "printer-zpl"], store.GetExposedPrinterIds().Order(StringComparer.Ordinal), StringComparer.Ordinal);
      Assert.Equal(new DeviceProfile("Front desk PC", "Shipping station\nsecond shift", "Dock 2"), store.GetDeviceProfile());
      Assert.Equal("Front desk laser", store.GetPrinterProfile("printer-a").DisplayName);
      AgentControlState control = store.LoadControlState();
      Assert.Equal(("SUPPORTED", 7L, true, "CANCEL_SPOOL"), (control.Support, control.DesiredState.Revision, control.DesiredState.DispatchPaused, control.LastCommand));
      Assert.Equal("cmd-cancel", Assert.Single(store.PendingControlReceipts()).CommandId);
      Assert.Equal("INVENTORY_REFRESHED", store.GetControlReceipt("cmd-refresh")!.Outcome);
      Assert.Null(store.GetControlReceipt("cmd-interrupted"));
      RealtimeSettings settings = store.LoadRealtimeSettings();
      Assert.Equal((3L, 2), (settings.Revision, settings.MaxConcurrentDownloads));
      Assert.Equal(new ServerTrust(true, "https://synthetic.invalid"), store.LoadServerTrust());
   }



   /// <summary>
   /// The oldest journal there is evidence of holds one events table of six columns. Migrating it adds the two later
   /// columns, every later table and index, and a summary for each job built from its latest event. Its events are all
   /// still there, unchanged, and now carry a scope: with no pairing saved, the scope a computer has before it is paired.
   /// A store on that computer then shows the three jobs, the one that never finished and the event still to be delivered.
   /// </summary>
   [Fact]
   public void Migrate_OldestShape_AddsColumnsAndJobSummaries()
   {
      string directory = NewFolder();
      try
      {
         _ = new LegacyStateBuilder(directory).WithJournal(LegacyStateBuilder.JournalOldest);
         Assert.Equal(["events", "sqlite_sequence"], JournalProbe.Tables(directory));
         IReadOnlyList<string> eventsBefore = JournalProbe.Rows(directory, "SELECT sequence,job_id,event_id,state,json,acknowledged FROM events ORDER BY sequence");

         Assert.IsType<StateStatus.Ready>(TestState.Migrate(directory));

         Assert.Equal(1, JournalProbe.Version(directory));
         Assert.Equal(eventsBefore, JournalProbe.Rows(directory, "SELECT sequence,job_id,event_id,state,json,acknowledged FROM events ORDER BY sequence"));
         Assert.Equal(["connection_id=unenrolled | recorded_at=NULL"], JournalProbe.Rows(directory, "SELECT DISTINCT connection_id,recorded_at FROM events"));
         Assert.Equal(
         [
            "job_id=job-a | state=SPOOLED | connection_id=unenrolled | failure_acknowledged=0 | history_pruned=0 | updated_at=NULL | created_at=NULL",
            "job_id=job-b | state=RECEIVED | connection_id=unenrolled | failure_acknowledged=0 | history_pruned=0 | updated_at=NULL | created_at=NULL",
            "job_id=job-c | state=FAILED | connection_id=unenrolled | failure_acknowledged=0 | history_pruned=0 | updated_at=NULL | created_at=NULL",
         ], JournalProbe.Rows(directory, "SELECT job_id,state,connection_id,failure_acknowledged,history_pruned,updated_at,created_at FROM local_jobs ORDER BY job_id"));
         using JournalStore store = TestState.OpenStoreAsFound(directory);
         Assert.Equal(["job-a", "job-b", "job-c"], store.RecentJobs().Select(job => job.JobId).Order(StringComparer.Ordinal), StringComparer.Ordinal);
         Assert.Equal(("job-b", "RECEIVED"), Assert.Single(store.Incomplete()));
         Assert.Equal("ev-c-failed", Assert.Single(store.Pending()).Event.EventId);
         Assert.Equal("7", store.GetJobDetails("job-a")!.Job.SpoolerJobId);
         Assert.Equal(1, store.NeedsAttentionCount());
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>
   /// Whatever shape a journal came in, it leaves the migration in one: the same tables, columns and indexes, written with
   /// the same statements, whether it was new, the oldest shape, the oldest shape with the shared-printer table of the
   /// first release, or a full 1.2.1 journal. Later steps can then be written against one schema.
   /// </summary>
   [Fact]
   public void Migrate_EveryShape_EndsInOneSchema()
   {
      string root = NewFolder();
      try
      {
         string empty = Path.Combine(root, "empty");
         string oldest = new LegacyStateBuilder(Path.Combine(root, "oldest")).WithJournal(LegacyStateBuilder.JournalOldest).DirectoryPath;
         string first = new LegacyStateBuilder(Path.Combine(root, "first")).WithJournal(LegacyStateBuilder.JournalOldest)
            .WithJournalSql("CREATE TABLE exposed_printers(printer_id TEXT PRIMARY KEY); ALTER TABLE events ADD COLUMN recorded_at TEXT").DirectoryPath;
         string latest = new LegacyStateBuilder(Path.Combine(root, "latest")).WithJournal(LegacyStateBuilder.Journal121).DirectoryPath;
         foreach(string directory in (string[])[empty, oldest, first, latest])
         {
            Assert.IsType<StateStatus.Ready>(TestState.Migrate(directory));
         }
         IReadOnlyList<string> schema = JournalProbe.Schema(empty);
         // Nine tables, the seventeen indexes the steps name, and the eight SQLite makes for primary keys and the unique event identifier.
         Assert.Equal(34, schema.Count);
         Assert.Equal(schema, JournalProbe.Schema(oldest));
         Assert.Equal(schema, JournalProbe.Schema(first));
         Assert.Equal(schema, JournalProbe.Schema(latest));
      }
      finally
      {
         JournalFiles.Delete(root);
      }
   }



   /// <summary>
   /// The five display indexes are written out in step 1, not built from the job list's sort expressions, so that the step
   /// keeps its meaning. This holds the two together: for each sortable field the index in a migrated journal is on
   /// exactly the expression the job list orders by. A sort expression changed without a migration step that replaces its
   /// index fails here.
   /// </summary>
   [Fact]
   public void DisplayIndexes_AreOnTheExpressionsTheJobListSortsBy()
   {
      string directory = NewFolder();
      try
      {
         _ = TestState.Migrate(directory);
         IReadOnlyList<string> expected = [.. JobSorting.Fields.Order(StringComparer.Ordinal).Select(field
            => "name=jobs_display_" + field + " | sql=CREATE INDEX jobs_display_" + field + " ON local_jobs(connection_id," + JobSorting.SqlColumn(field) + " COLLATE NOCASE DESC,job_id DESC)")];
         Assert.Equal(expected, JournalProbe.Rows(directory, "SELECT name,sql FROM sqlite_master WHERE name LIKE 'jobs!_display!_%' ESCAPE '!' ORDER BY name"));
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>
   /// Rows from before events carried a scope get a scope of their own when the saved pairing cannot be read: nobody can
   /// say which pairing they belonged to, so they must not appear under one that is saved later. The pairing file here is
   /// not something DPAPI wrote. The events are kept, all under one scope that starts "unreadable-", and a store opened
   /// for recovery, as the service opens it, shows none of them.
   /// </summary>
   [Fact]
   public void Migrate_OldestShape_UnreadablePairing_SetsTheRowsApart()
   {
      string directory = NewFolder();
      try
      {
         _ = new LegacyStateBuilder(directory).WithJournal(LegacyStateBuilder.JournalOldest).WithFile("connection.dpapi", [1, 2, 3, 4]);

         Assert.IsType<StateStatus.Ready>(TestState.Migrate(directory));

         string scope = Assert.Single(JournalProbe.Rows(directory, "SELECT DISTINCT connection_id FROM events"));
         Assert.StartsWith("connection_id=unreadable-", scope, StringComparison.Ordinal);
         Assert.Equal(6L, JournalProbe.Scalar(directory, "SELECT COUNT(*) FROM events"));
         Assert.Equal(3L, JournalProbe.Scalar(directory, "SELECT COUNT(*) FROM local_jobs"));
         using JournalStore store = TestState.OpenStore(directory, recoverUnreadableConfiguration: true);
         Assert.Empty(store.RecentJobs());
         Assert.Empty(store.Pending());
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>
   /// A step that fails takes the whole migration with it. Step 1 runs first here and does all its work on the oldest
   /// journal; a second step, added by the test, creates a table and then throws. Afterwards the journal is as it was:
   /// version 0, the one original table, the same rows. The failure reaches the caller. Migrating again with the product's
   /// steps then succeeds, which is what the next start of the service does.
   /// </summary>
   [Fact]
   public void Migrate_StepThrows_VersionAndTablesUnchanged()
   {
      string directory = NewFolder();
      try
      {
         _ = new LegacyStateBuilder(directory).WithJournal(LegacyStateBuilder.JournalOldest);
         IReadOnlyList<string> schemaBefore = JournalProbe.Schema(directory);
         IReadOnlyDictionary<string, string> rowsBefore = JournalProbe.RowDigests(directory);
         bool firstStepRan = false;
         IReadOnlyList<Action<JournalMigrationContext>> steps =
         [
            context =>
            {
               TestState.MigrationSteps[0](context);
               firstStepRan = context.HasTable("local_jobs");
            },
            context =>
            {
               context.Execute("CREATE TABLE added_by_the_failing_step(value TEXT)");
               throw new InvalidOperationException("Synthetic failure in a migration step.");
            },
         ];

         InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => TestState.Migrate(directory, steps));

         Assert.Equal("Synthetic failure in a migration step.", failure.Message);
         Assert.True(firstStepRan);
         Assert.Equal(0, JournalProbe.Version(directory));
         Assert.Equal(schemaBefore, JournalProbe.Schema(directory));
         Assert.Equal(rowsBefore, JournalProbe.RowDigests(directory));
         Assert.IsType<StateStatus.Ready>(TestState.Migrate(directory));
         Assert.Equal(3L, JournalProbe.Scalar(directory, "SELECT COUNT(*) FROM local_jobs"));
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>
   /// A journal at a higher version than this build knows belongs to a newer build. The migration says so, with both
   /// versions, and touches nothing: every file in the folder (the journal, the pairing, a session file, a staged
   /// document in a folder beneath) has the same bytes and the same last-write time afterwards, and no file has appeared.
   /// The version is read through a read-only connection; an ordinary one would leave a log and an index file behind.
   /// </summary>
   [Fact]
   public void Migrate_NewerVersion_FileBytesUnchanged()
   {
      string directory = NewFolder();
      try
      {
         int newer = TestState.SchemaVersion + 1;
         LegacyStateBuilder state = new LegacyStateBuilder(directory).WithJournal(LegacyStateBuilder.Journal121).WithPairing(CurrentPairing)
            .WithFile("qqq-session.dpapi", [9, 8, 7]).WithJournalSql("CREATE TABLE added_by_the_newer_build(value TEXT); PRAGMA user_version=" + newer.ToString(System.Globalization.CultureInfo.InvariantCulture));
         _ = Directory.CreateDirectory(Path.Combine(directory, "staging"));
         _ = state.WithFile(Path.Combine("staging", "document.pdf"), [37, 80, 68, 70]);
         IReadOnlyDictionary<string, string> hashesBefore = JournalProbe.FileHashes(directory);
         IReadOnlyDictionary<string, DateTime> writtenBefore = LastWrites(directory);
         Assert.Equal(["connection.dpapi", "journal.db", "qqq-session.dpapi", Path.Combine("staging", "document.pdf")], hashesBefore.Keys, StringComparer.Ordinal);

         StateStatus status = TestState.Migrate(directory);

         Assert.Equal(new StateStatus.NewerBuild(FoundVersion: newer, SupportedVersion: TestState.SchemaVersion), status);
         Assert.Equal(StateStatus.NewerBuildText, status.Refusal);
         Assert.Equal(hashesBefore, JournalProbe.FileHashes(directory));
         Assert.Equal(writtenBefore, LastWrites(directory));
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>
   /// A service that is stopped leaves its write-ahead log behind, and what it wrote last may be only there. Here a newer
   /// build raised the version and was killed: the journal file alone still says this build's version (its header is read
   /// to show that) and the log says the next. The migration must find the newer version all the same, so it reads the log;
   /// that makes SQLite create its index file beside the journal, which holds no data. The journal and the log keep their bytes.
   /// </summary>
   [Fact]
   public void Migrate_NewerVersionOnlyInTheLog_IsFound_JournalAndLogUnchanged()
   {
      string root = NewFolder();
      string live = Path.Combine(root, "live");
      string killed = Path.Combine(root, "killed");
      int newer = TestState.SchemaVersion + 1;
      try
      {
         _ = TestState.Migrate(live);
         _ = Directory.CreateDirectory(killed);
         using(SqliteConnection newerBuild = TestState.OpenJournal(live))
         {
            using SqliteCommand command = newerBuild.CreateCommand();
            command.CommandText = "CREATE TABLE added_by_the_newer_build(value TEXT); PRAGMA user_version=" + newer.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ = command.ExecuteNonQuery();
            // The files as they are while the newer build still has them open: what is on disk if it never closes them.
            File.WriteAllBytes(Path.Combine(killed, "journal.db"), JournalProbe.ReadShared(Path.Combine(live, "journal.db")));
            File.WriteAllBytes(Path.Combine(killed, "journal.db-wal"), JournalProbe.ReadShared(Path.Combine(live, "journal.db-wal")));
         }
         IReadOnlyDictionary<string, string> hashesBefore = JournalProbe.FileHashes(killed);
         Assert.Equal(TestState.SchemaVersion, BinaryPrimitives.ReadInt32BigEndian(File.ReadAllBytes(Path.Combine(killed, "journal.db")).AsSpan(60, 4)));

         StateStatus status = TestState.Migrate(killed);

         Assert.Equal(new StateStatus.NewerBuild(FoundVersion: newer, SupportedVersion: TestState.SchemaVersion), status);
         IReadOnlyDictionary<string, string> hashesAfter = JournalProbe.FileHashes(killed);
         Assert.Equal(["journal.db", "journal.db-shm", "journal.db-wal"], hashesAfter.Keys, StringComparer.Ordinal);
         Assert.Equal(hashesBefore["journal.db"], hashesAfter["journal.db"]);
         Assert.Equal(hashesBefore["journal.db-wal"], hashesAfter["journal.db-wal"]);
      }
      finally
      {
         JournalFiles.Delete(root);
      }
   }



   /// <summary>
   /// Migrating a migrated journal does nothing. The second call is given a step that counts its runs, and it never runs;
   /// the journal file has the same bytes afterwards and no other file has appeared.
   /// </summary>
   [Fact]
   public void Migrate_Twice_SecondDoesNothing()
   {
      string directory = NewFolder();
      try
      {
         _ = new LegacyStateBuilder(directory).WithJournal(LegacyStateBuilder.JournalOldest);
         Assert.IsType<StateStatus.Ready>(TestState.Migrate(directory));
         IReadOnlyDictionary<string, string> hashesBefore = JournalProbe.FileHashes(directory);
         int runs = 0;

         StateStatus second = TestState.Migrate(directory, [_ => runs++]);

         Assert.IsType<StateStatus.Ready>(second);
         Assert.Equal(0, runs);
         Assert.Equal(["journal.db"], hashesBefore.Keys, StringComparer.Ordinal);
         Assert.Equal(hashesBefore, JournalProbe.FileHashes(directory));
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>
   /// Two callers migrate one journal at the same moment and the step runs once. The first caller's step signals that it
   /// has started and then waits to be released, while it holds the journal's lock and its open transaction. The second
   /// caller is started only then, and is seen to block: the only thing it can be waiting for is that lock. It has not
   /// returned while the first is held. Once the first is released and has committed, the second reads the version, finds
   /// the journal migrated and returns without running anything. Nothing here waits for a length of time.
   /// </summary>
   [Fact]
   public void Migrate_TwoCallersAtOnce_RunsOnce()
   {
      string directory = NewFolder();
      try
      {
         using ManualResetEventSlim started = new();
         using ManualResetEventSlim released = new();
         int runs = 0;
         IReadOnlyList<Action<JournalMigrationContext>> steps =
         [
            context =>
            {
               _ = Interlocked.Increment(ref runs);
               started.Set();
               if(!released.Wait(TimeSpan.FromSeconds(30)))
               { throw new TimeoutException("The test never released the first migration."); }
               TestState.MigrationSteps[0](context);
            },
         ];
         var results = new StateStatus?[2];
         var failures = new Exception?[2];
         Thread first = MigratingThread(directory, steps, results, failures, 0);
         Thread second = MigratingThread(directory, steps, results, failures, 1);
         first.Start();
         Assert.True(started.Wait(TimeSpan.FromSeconds(30)), "The first migration never reached its step.");
         second.Start();
         bool secondWaited = SpinWait.SpinUntil(() => second.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(30));
         StateStatus? secondWhileFirstIsHeld = results[1];
         released.Set();
         first.Join();
         second.Join();

         Assert.True(secondWaited, "The second migration never came to wait for the journal's lock.");
         Assert.Null(secondWhileFirstIsHeld);
         Assert.Equal([null, null], failures);
         Assert.IsType<StateStatus.Ready>(results[0]);
         Assert.IsType<StateStatus.Ready>(results[1]);
         Assert.Equal(1, runs);
         Assert.Equal(1, JournalProbe.Version(directory));
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>
   /// A journal whose version is below zero was not written by any build, and no list of steps says how to go on from
   /// there. The migration refuses it and leaves it alone, where counting on from a negative number would have run steps
   /// that do not exist.
   /// </summary>
   [Fact]
   public void Migrate_NegativeVersion_IsRefused_JournalUnchanged()
   {
      string directory = NewFolder();
      try
      {
         _ = new LegacyStateBuilder(directory).WithJournal(LegacyStateBuilder.JournalOldest).WithJournalSql("PRAGMA user_version=-3");
         IReadOnlyDictionary<string, string> hashesBefore = JournalProbe.FileHashes(directory);

         _ = Assert.Throws<InvalidDataException>(() => TestState.Migrate(directory));

         Assert.Equal(hashesBefore, JournalProbe.FileHashes(directory));
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>
   /// A journal that cannot be opened fails the migration with SQLite's own error, which is what stops the service from
   /// starting on state it cannot read. Here the journal's name is taken by a folder. Nothing is left open: the folder can
   /// be deleted at once.
   /// </summary>
   [Fact]
   public void Migrate_JournalCannotBeOpened_Fails()
   {
      string directory = NewFolder();
      try
      {
         _ = Directory.CreateDirectory(Path.Combine(directory, "journal.db"));

         SqliteException failure = Assert.Throws<SqliteException>(() => TestState.Migrate(directory));

         Assert.Equal(14, failure.SqliteErrorCode);
         Directory.Delete(directory, recursive: true);
         Assert.False(Directory.Exists(directory));
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>
   /// The service migrates the journal itself when it starts. The harness migrates when it is built, for the tests that
   /// never run the service loop, so the journal is deleted again here before the loop starts, and a store then refuses to
   /// open. The loop must make the journal and bring it to the current version before it answers anything: the snapshot
   /// it gives says the computer is paired, which it can only know by opening a store.
   /// </summary>
   [Fact]
   public async Task ServiceStart_MigratesTheJournalBeforeItOpensAStore()
   {
      using SessionFixture fixture = new();
      string directory = fixture.Local.DirectoryPath;
      using SingleServerHarness runtime = fixture.Runtime();
      JournalFiles.Release(directory);
      foreach(string file in Directory.EnumerateFiles(directory, "journal.db*"))
      { File.Delete(file); }
      Assert.Equal(JournalSchema.NotMigratedText, Assert.Throws<InvalidOperationException>(runtime.GetSnapshot).Message);
      using CancellationTokenSource stop = new(TimeSpan.FromSeconds(60));
      Task running = runtime.RunAsync(stop.Token);
      try
      {
         string reply = await runtime.PipeAsync(new PipeRequest("snapshot"), stop.Token);
         Assert.True(JsonSerializer.Deserialize<AgentSnapshot>(reply, PrintApiClient.JsonOptions)!.Enrolled);
         Assert.Equal(TestState.SchemaVersion, JournalProbe.Version(directory));
      }
      finally
      {
         await stop.CancelAsync();
         try
         { await running; }
         catch(OperationCanceledException) { }
      }
   }


   /// <summary>The three cases of the stored state say the same thing in the form the management contract's snapshot carries, and only a ready state refuses nothing.</summary>
   [Fact]
   public void StateStatus_MapsToTheSnapshotsStoredState()
   {
      Assert.Equal(new Windows.Management.StoredState(Windows.Management.StoredState.Ready), new StateStatus.Ready().ToStoredState());
      Assert.Null(new StateStatus.Ready().Refusal);
      StateStatus incomplete = new StateStatus.UpgradeIncomplete("connection.dpapi");
      Assert.Equal(new Windows.Management.StoredState(Windows.Management.StoredState.UpgradeIncomplete, File: "connection.dpapi"), incomplete.ToStoredState());
      Assert.Equal(StateStatus.UpgradeIncompleteText, incomplete.Refusal);
      StateStatus newer = new StateStatus.NewerBuild(4, 1);
      Assert.Equal(new Windows.Management.StoredState(Windows.Management.StoredState.NewerBuild, FoundVersion: 4, SupportedVersion: 1), newer.ToStoredState());
      Assert.Equal(StateStatus.NewerBuildText, newer.Refusal);
   }



   /// <summary>A thread that migrates the folder with the given steps and records what it returned or how it failed, at its own position.</summary>
   private static Thread MigratingThread(string directory, IReadOnlyList<Action<JournalMigrationContext>> steps, StateStatus?[] results, Exception?[] failures, int position) => new(() =>
   {
      try
      {
         results[position] = TestState.Migrate(directory, steps);
      }
      catch(Exception exception) when(exception is SqliteException or IOException or InvalidOperationException or TimeoutException)
      {
         failures[position] = exception;
      }
   });



   /// <summary>The last-write time of every file in the folder and beneath it, by relative path.</summary>
   private static SortedDictionary<string, DateTime> LastWrites(string directory)
   {
      SortedDictionary<string, DateTime> written = new(StringComparer.OrdinalIgnoreCase);
      foreach(string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
      {
         written[Path.GetRelativePath(directory, file)] = File.GetLastWriteTimeUtc(file);
      }
      return written;
   }



   /// <summary>A path for a state folder of this test's own; nothing is created.</summary>
   private static string NewFolder() => Path.Combine(Path.GetTempPath(), "qqq-journal-migration-" + Guid.NewGuid().ToString("N"));
}
