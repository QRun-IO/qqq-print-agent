/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// An installation of an earlier release, upgraded in place. The release depends on that: a computer running 1.2.x
/// must come out of the first start of this build with its pairing, its job history and its settings as it left them.
/// <see cref="JournalMigrationTests"/> shows it for a journal that was closed cleanly. These tests show it for the
/// states a real installation can also be in: a journal whose latest rows are only in the write-ahead log, a journal in
/// each shape between the oldest and 1.2.1, and a whole state folder under the service loop with jobs left in flight.
/// </summary>
public sealed class JournalUpgradeTests
{
   /// <summary>The pairing whose scope most rows of the 1.2.1 fixture carry.</summary>
   private static readonly AgentConfiguration Pairing = new("https://synthetic.invalid", "device", "synthetic-token");



   /// <summary>A 1.0.x journal: no certificate exception table, which arrived in 1.1.0.</summary>
   private const string WithoutTrust = "DROP TABLE server_trust;";



   /// <summary>A journal from before realtime settings were stored.</summary>
   private const string WithoutRealtime = WithoutTrust + " DROP TABLE realtime_settings;";



   /// <summary>A journal from before remote control: no control state and no control commands, with their two indexes.</summary>
   private const string WithoutControl = WithoutRealtime + " DROP TABLE control_commands; DROP TABLE control_state;";



   /// <summary>A journal from before job summaries had a creation time: without that column and the five indexes the job list sorts by.</summary>
   private const string WithoutCreationTime = WithoutControl
      + " DROP INDEX jobs_display_createdAt; DROP INDEX jobs_display_title; DROP INDEX jobs_display_printerName; DROP INDEX jobs_display_status; DROP INDEX jobs_display_format;"
      + " ALTER TABLE local_jobs DROP COLUMN created_at;";



   /// <summary>A journal from before the first indexes and the operator's own names: the events and summaries of the release that introduced the scope, and the shared-printer list.</summary>
   private const string WithoutIndexes = WithoutCreationTime
      + " DROP TABLE local_metadata; DROP INDEX events_outbox; DROP INDEX events_job; DROP INDEX jobs_recent; DROP INDEX jobs_status; DROP INDEX jobs_printer;"
      + " DROP INDEX jobs_attention; DROP INDEX jobs_retention; DROP INDEX jobs_title_search; DROP INDEX jobs_printer_search; DROP INDEX jobs_id_search;";



   /// <summary>
   /// A 1.2.1 service was killed with everything it had written still in the write-ahead log: the journal file by itself
   /// has no table at all. The migration reads the log, and the journal comes out with the schema and, table by table,
   /// the rows of a 1.2.1 journal that was closed cleanly.
   /// </summary>
   [Fact]
   public void Journal121_KilledWithItsRowsOnlyInTheLog_KeepsEveryRow()
   {
      string root = NewFolder();
      try
      {
         string clean = new LegacyStateBuilder(Path.Combine(root, "clean")).WithJournal(LegacyStateBuilder.Journal121).DirectoryPath;
         string killed = new LegacyStateBuilder(Path.Combine(root, "killed")).WithJournalLeftByAKill(LegacyStateBuilder.Journal121).DirectoryPath;
         string fileAlone = new LegacyStateBuilder(Path.Combine(root, "file-alone")).WithFile("journal.db", File.ReadAllBytes(Path.Combine(killed, "journal.db"))).DirectoryPath;
         Assert.Empty(JournalProbe.Tables(fileAlone));
         Assert.True(new FileInfo(Path.Combine(killed, "journal.db-wal")).Length > 0);

         Assert.IsType<StateStatus.Ready>(TestState.Migrate(killed));

         Assert.Equal(1, JournalProbe.Version(killed));
         Assert.Equal(JournalProbe.RowDigests(clean), JournalProbe.RowDigests(killed));
         Assert.Equal(JournalProbe.Schema(clean), JournalProbe.Schema(killed));
         Assert.Equal(9, JournalProbe.RowDigests(killed).Count);
      }
      finally
      {
         JournalFiles.Delete(root);
      }
   }



   /// <summary>
   /// The shapes a journal had between the oldest and 1.2.1, each made by taking away from the 1.2.1 journal what the
   /// later releases added: the certificate exception table (so, a 1.0.x journal), a 1.2.1 journal that never used any of
   /// the three tables created on first use, the realtime settings, the control tables, the creation time with its five
   /// indexes, and the earlier indexes with the local names. Each ends in the one schema a new journal has, keeps every
   /// row it had, and is read by the current store. A summary that had no creation time gets one.
   /// </summary>
   [Fact]
   public void IntermediateShapes_EndInOneSchema_AndKeepTheirRows()
   {
      (string Sql, bool HasCreationTime)[] shapes =
      [
         (WithoutTrust, true),
         (WithoutTrust + " DROP TABLE realtime_settings; DROP TABLE local_metadata;", true),
         (WithoutRealtime, true),
         (WithoutControl, true),
         (WithoutCreationTime, false),
         (WithoutIndexes, false),
      ];
      string root = NewFolder();
      try
      {
         string reference = Path.Combine(root, "reference");
         _ = TestState.Migrate(reference);
         IReadOnlyList<string> schema = JournalProbe.Schema(reference);
         // What each shape has left of the 34 tables and indexes of a 1.2.1 journal; the last is four tables and the three indexes SQLite makes for their keys.
         Assert.Equal([32, 28, 30, 24, 19, 7], shapes.Select((shape, position) => SchemaObjects(Path.Combine(root, "count-" + position), shape.Sql)));
         foreach((string sql, bool hasCreationTime) in shapes)
         {
            AssertUpgrades(Path.Combine(root, Guid.NewGuid().ToString("N")), sql, hasCreationTime, schema);
         }
      }
      finally
      {
         JournalFiles.Delete(root);
      }
   }



   /// <summary>
   /// A 1.2.1 state folder as a killed service left it: the pairing, the full journal, a job whose submission to Windows
   /// had begun (SUBMITTING) and a job that had only been received. This build's service loop starts on it. It migrates
   /// the journal and then settles the two jobs by the rule that has always held: the interrupted submission becomes
   /// OUTCOME_UNKNOWN, because only an operator can tell whether paper came out, and the job Windows was never asked to
   /// print becomes FAILED. Nothing is printed again. The snapshot shows the pairing, every job of the pairing with the
   /// state 1.2.1 left it in, the shared printers with the operator's names, the computer's own name and the control
   /// state; the certificate exception is still there. No session is created, because none was saved. The events 1.2.1
   /// wrote are unchanged, and the only new ones are the two that settle those jobs.
   /// </summary>
   [Fact]
   public async Task Installation121_FirstStart_KeepsPairingHistoryAndSettings_AndSettlesJobsLeftInFlight()
   {
      string directory = NewFolder();
      ConcurrentQueue<string> logs = new();
      int sessions = 0;
      try
      {
         using SingleServerHarness runtime = new(directory, logs.Enqueue, new EnrolledStateFixture.SyntheticPrinter(), sessionFactory: _ =>
         {
            sessions++;
            throw new InvalidOperationException("No session may be created: none is saved.");
         });
         Installation121WithJobsInFlight(directory);
         IReadOnlyDictionary<string, string> rowsBefore = JournalProbe.RowDigests(directory);
         IReadOnlyList<string> eventsBefore = JournalProbe.Rows(directory, "SELECT * FROM events ORDER BY sequence");
         using CancellationTokenSource stop = new(TimeSpan.FromSeconds(60));
         Task running = runtime.RunAsync(stop.Token);
         try
         {
            AssertKept(JsonSerializer.Deserialize<AgentSnapshot>(await runtime.PipeAsync(new PipeRequest("snapshot"), stop.Token), PrintApiClient.JsonOptions)!);
            Assert.Equal(new ServerTrust(true, "https://synthetic.invalid"), JsonSerializer.Deserialize<ServerTrust>(await runtime.PipeAsync(new PipeRequest("get-server-trust"), stop.Token), PrintApiClient.JsonOptions));
            Assert.Contains("Agent stopped during native submission; inspect the printer before retrying.", await runtime.PipeAsync(new PipeRequest("job-details", JobId: "job-1005"), stop.Token), StringComparison.Ordinal);
            Assert.Contains("Agent stopped before native submission.", await runtime.PipeAsync(new PipeRequest("job-details", JobId: "job-1006"), stop.Token), StringComparison.Ordinal);
         }
         finally
         {
            await stop.CancelAsync();
            try
            { await running; }
            catch(OperationCanceledException) { }
         }
         Assert.Equal(0, sessions);
         Assert.Equal(1, JournalProbe.Version(directory));
         Assert.Equal(eventsBefore, JournalProbe.Rows(directory, "SELECT * FROM events WHERE sequence<=12 ORDER BY sequence"));
         Assert.Equal(["job_id=job-1005 | state=OUTCOME_UNKNOWN | acknowledged=0", "job_id=job-1006 | state=FAILED | acknowledged=0"],
            JournalProbe.Rows(directory, "SELECT job_id,state,acknowledged FROM events WHERE sequence>12 ORDER BY job_id"));
         IReadOnlyDictionary<string, string> rowsAfter = JournalProbe.RowDigests(directory);
         Assert.All((string[])["exposed_printers", "local_metadata", "realtime_settings", "server_trust", "control_state"], table => Assert.Equal((table, rowsBefore[table]), (table, rowsAfter[table])));
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>
   /// Replaces the journal the harness made in the folder with a 1.2.1 installation's, so that the service loop is the
   /// first thing of this build to touch it, and saves the pairing as 1.2.1 did. Two jobs of that pairing are added in
   /// the states a killed service leaves: job-1005 with its SUBMITTING event recorded, job-1006 received and no further.
   /// </summary>
   private static void Installation121WithJobsInFlight(string directory)
   {
      foreach(string file in Directory.EnumerateFiles(directory, "journal.db*"))
      { File.Delete(file); }
      LegacyStateBuilder state = new LegacyStateBuilder(directory).WithJournal(LegacyStateBuilder.Journal121).WithPairing(Pairing);
      string scope = (string)JournalProbe.Scalar(directory, "SELECT connection_id FROM local_jobs WHERE job_id='job-1001'")!;
      _ = state.WithJournalSql($$"""
         INSERT INTO events(job_id,event_id,state,json,acknowledged,recorded_at,connection_id) VALUES
          ('job-1005','ev-1005-received','RECEIVED','{"eventId":"ev-1005-received","status":"RECEIVED"}',1,'2026-11-06T16:00:00.0000000+00:00','{{scope}}'),
          ('job-1005','ev-1005-submitting','SUBMITTING','{"eventId":"ev-1005-submitting","status":"SUBMITTING"}',1,'2026-11-06T16:00:01.0000000+00:00','{{scope}}'),
          ('job-1006','ev-1006-received','RECEIVED','{"eventId":"ev-1006-received","status":"RECEIVED"}',1,'2026-11-06T16:01:00.0000000+00:00','{{scope}}');
         INSERT INTO local_jobs(connection_id,job_id,printer_id,printer_name,title,format,state,last_event_json,updated_at,failure_acknowledged,history_pruned,created_at) VALUES
          ('{{scope}}','job-1005','printer-a','Front desk laser','Packing slip 1005','PDF','SUBMITTING','{"eventId":"ev-1005-submitting","status":"SUBMITTING"}','2026-11-06T16:00:01.0000000+00:00',0,0,'2026-11-06T15:59:58.0000000+00:00'),
          ('{{scope}}','job-1006','printer-a','Front desk laser','Packing slip 1006','PDF','RECEIVED','{"eventId":"ev-1006-received","status":"RECEIVED"}','2026-11-06T16:01:00.0000000+00:00',0,0,'2026-11-06T16:00:58.0000000+00:00');
         """);
      Assert.Equal(0, JournalProbe.Version(directory));
   }



   /// <summary>What the first snapshot after the upgrade must show of the 1.2.1 installation; each line names rows of the fixture or the two jobs left in flight.</summary>
   private static void AssertKept(AgentSnapshot snapshot)
   {
      Assert.Equal((true, "https://synthetic.invalid", "device"), (snapshot.Enrolled, snapshot.ServerUrl, snapshot.DeviceId));
      Assert.Equal(
      [
         "job-0950=SPOOLED", "job-1001=SPOOLED", "job-1002=FAILED", "job-1003=FAILED reviewed", "job-1004=OUTCOME_UNKNOWN", "job-1005=OUTCOME_UNKNOWN", "job-1006=FAILED",
      ], snapshot.Jobs.Select(job => job.JobId + "=" + job.Status + (job.FailureAcknowledged ? " reviewed" : string.Empty)).Order(StringComparer.Ordinal), StringComparer.Ordinal);
      Assert.Equal(["printer-a=Front desk laser", "printer-zpl=Label printer"],
         snapshot.Printers.Where(printer => printer.Exposed).Select(printer => printer.PrinterId + "=" + printer.DisplayName).Order(StringComparer.Ordinal), StringComparer.Ordinal);
      Assert.Equal(new DeviceProfile("Front desk PC", "Shipping station\nsecond shift", "Dock 2"), snapshot.DeviceProfile);
      Assert.Equal((7L, true, "CANCEL_SPOOL"), (snapshot.Control!.DesiredState.Revision, snapshot.Control.DesiredState.DispatchPaused, snapshot.Control.LastCommand));
   }



   /// <summary>
   /// Migrates a 1.2.1 journal that was first put into an earlier shape, and requires the one schema, the rows it had and
   /// a store that reads them. The rows of every table the shape had are compared by digest. Where the shape had no
   /// creation time, the summaries are compared by the columns they had, and every summary must have got a creation time.
   /// </summary>
   private static void AssertUpgrades(string directory, string shape, bool hasCreationTime, IReadOnlyList<string> schema)
   {
      const string Summaries = "SELECT connection_id,job_id,printer_id,printer_name,title,format,state,last_event_json,updated_at,failure_acknowledged,history_pruned FROM local_jobs ORDER BY connection_id,job_id";
      _ = new LegacyStateBuilder(directory).WithJournal(LegacyStateBuilder.Journal121).WithPairing(Pairing).WithJournalSql(shape);
      IReadOnlyDictionary<string, string> rowsBefore = JournalProbe.RowDigests(directory);
      IReadOnlyList<string> summariesBefore = JournalProbe.Rows(directory, Summaries);

      Assert.IsType<StateStatus.Ready>(TestState.Migrate(directory));

      Assert.Equal(schema, JournalProbe.Schema(directory));
      IReadOnlyDictionary<string, string> rowsAfter = JournalProbe.RowDigests(directory);
      Assert.All(rowsBefore.Keys.Where(table => hasCreationTime || !string.Equals(table, "local_jobs", StringComparison.Ordinal)), table => Assert.Equal((table, rowsBefore[table]), (table, rowsAfter[table])));
      Assert.Equal(summariesBefore, JournalProbe.Rows(directory, Summaries));
      Assert.Equal(0L, JournalProbe.Scalar(directory, "SELECT COUNT(*) FROM local_jobs WHERE created_at IS NULL"));
      using JournalStore store = TestState.OpenStoreAsFound(directory);
      Assert.Equal(["job-0950", "job-1001", "job-1002", "job-1003", "job-1004"], store.RecentJobs().Select(job => job.JobId).Order(StringComparer.Ordinal), StringComparer.Ordinal);
      Assert.Equal(["ev-1002-failed", "ev-1004-unknown"], store.Pending().Select(pending => pending.Event.EventId), StringComparer.Ordinal);
      Assert.Equal(["printer-a", "printer-zpl"], store.GetExposedPrinterIds().Order(StringComparer.Ordinal), StringComparer.Ordinal);
   }



   /// <summary>How many tables and indexes a 1.2.1 journal has once it is put into a shape, which shows that the shape took away what it says.</summary>
   private static int SchemaObjects(string directory, string shape)
      => JournalProbe.Schema(new LegacyStateBuilder(directory).WithJournal(LegacyStateBuilder.Journal121).WithJournalSql(shape).DirectoryPath).Count;



   /// <summary>A path for a state folder of this test's own; nothing is created.</summary>
   private static string NewFolder() => Path.Combine(Path.GetTempPath(), "qqq-journal-upgrade-" + Guid.NewGuid().ToString("N"));
}
