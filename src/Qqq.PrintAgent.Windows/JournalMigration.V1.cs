/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Step 1: every schema change the 1.x releases made, as one step. Those releases had no schema version. Each store they
/// opened created what was missing and added the columns a later release had introduced, and three tables were created
/// only when first used. A journal can therefore arrive in any shape between a single six-column events table and the
/// full 1.2.1 schema, with any of the late tables absent. This step creates each table, column and index only when it is
/// missing, with the statements those releases used, so every journal leaves it in one shape whatever shape it came in.
/// <para>
/// The statements are written out here and not built from the code that queries the tables. A migration step has to mean
/// the same thing for as long as it exists; if it borrowed, say, the sort expressions of the job list, a later change to
/// those would silently change what a new installation gets while existing ones kept the old index.
/// </para>
/// </summary>
internal static partial class JournalMigration
{
   /// <summary>Brings a journal of any 1.x shape, or an empty one, to schema version 1.</summary>
   internal static void V1(JournalMigrationContext context)
   {
      CreateEvents(context);
      CreateJobSummaries(context);
      CreateJobIndexes(context);
      CreateControlAndSettings(context);
   }



   /// <summary>
   /// The event outbox and the shared-printer list, as the first release made them, then the two columns later releases
   /// added to events. Rows written before events carried a connection scope get the scope of the pairing that is saved
   /// now, which is what every 1.x store did on its next open; only a journal last opened by a release from before the
   /// scope column can still hold such rows.
   /// </summary>
   private static void CreateEvents(JournalMigrationContext context)
   {
      context.Execute("""
         CREATE TABLE IF NOT EXISTS events(sequence INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL, event_id TEXT NOT NULL UNIQUE, state TEXT NOT NULL, json TEXT NOT NULL, acknowledged INTEGER NOT NULL DEFAULT 0);
         CREATE TABLE IF NOT EXISTS exposed_printers(printer_id TEXT PRIMARY KEY);
         """);
      if(!context.HasColumn("events", "recorded_at"))
      {
         context.Execute("ALTER TABLE events ADD COLUMN recorded_at TEXT");
      }
      if(!context.HasColumn("events", "connection_id"))
      {
         context.Execute("ALTER TABLE events ADD COLUMN connection_id TEXT");
      }
      if(context.Scalar("SELECT EXISTS(SELECT 1 FROM events WHERE connection_id IS NULL)") != 0)
      {
         context.Execute("UPDATE events SET connection_id=$scope WHERE connection_id IS NULL", ("$scope", AgentStore.ScopeForUnscopedRows(context.Directory)));
      }
   }



   /// <summary>
   /// The per-job summary table. When it did not exist, each job's summary is built from its latest event; that happens
   /// only then, so summaries a release has since maintained are never overwritten from the outbox. The creation time is a
   /// later column: where it is missing it is added and filled from the job's first recorded event, or from the summary's
   /// own time when the events are gone.
   /// </summary>
   private static void CreateJobSummaries(JournalMigrationContext context)
   {
      bool summariesMissing = !context.HasTable("local_jobs");
      context.Execute("CREATE TABLE IF NOT EXISTS local_jobs(connection_id TEXT NOT NULL, job_id TEXT NOT NULL, printer_id TEXT, printer_name TEXT, title TEXT, format TEXT, state TEXT, last_event_json TEXT, updated_at TEXT, failure_acknowledged INTEGER NOT NULL DEFAULT 0, history_pruned INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(connection_id,job_id));");
      if(summariesMissing)
      {
         context.Execute("INSERT OR IGNORE INTO local_jobs(connection_id,job_id,state,last_event_json,updated_at) SELECT connection_id,job_id,state,json,recorded_at FROM events WHERE sequence IN (SELECT MAX(sequence) FROM events GROUP BY connection_id,job_id);");
      }
      if(!context.HasColumn("local_jobs", "created_at"))
      {
         context.Execute("""
            ALTER TABLE local_jobs ADD COLUMN created_at TEXT;
            UPDATE local_jobs SET created_at=COALESCE(
               (SELECT recorded_at FROM events WHERE events.connection_id=local_jobs.connection_id
                AND events.job_id=local_jobs.job_id AND recorded_at IS NOT NULL ORDER BY sequence LIMIT 1),updated_at);
            """);
      }
   }



   /// <summary>
   /// The indexes that keep outbox drains, the status and printer filters, the attention list, retention, the prefix
   /// searches and each sort order of the job list fast as history grows. The five display indexes repeat the expressions
   /// the job list sorts by; a test compares the two, so a change to a sort expression cannot leave its index behind.
   /// </summary>
   private static void CreateJobIndexes(JournalMigrationContext context)
   {
      context.Execute("""
         CREATE INDEX IF NOT EXISTS events_outbox ON events(connection_id,acknowledged,sequence);
         CREATE INDEX IF NOT EXISTS events_job ON events(connection_id,job_id,sequence);
         CREATE INDEX IF NOT EXISTS jobs_recent ON local_jobs(connection_id,updated_at DESC,job_id DESC);
         CREATE INDEX IF NOT EXISTS jobs_status ON local_jobs(connection_id,state,failure_acknowledged,updated_at DESC,job_id DESC);
         CREATE INDEX IF NOT EXISTS jobs_printer ON local_jobs(connection_id,printer_id,updated_at DESC,job_id DESC);
         CREATE INDEX IF NOT EXISTS jobs_attention ON local_jobs(connection_id,CASE WHEN state IN ('FAILED','OUTCOME_UNKNOWN') AND failure_acknowledged=0 THEN 0 ELSE 1 END,updated_at DESC,job_id DESC);
         CREATE INDEX IF NOT EXISTS jobs_retention ON local_jobs(connection_id,history_pruned,updated_at DESC,job_id DESC);
         CREATE INDEX IF NOT EXISTS jobs_title_search ON local_jobs(connection_id,title COLLATE NOCASE);
         CREATE INDEX IF NOT EXISTS jobs_printer_search ON local_jobs(connection_id,printer_name COLLATE NOCASE);
         CREATE INDEX IF NOT EXISTS jobs_id_search ON local_jobs(connection_id,job_id COLLATE NOCASE);
         CREATE INDEX IF NOT EXISTS jobs_display_createdAt ON local_jobs(connection_id,created_at COLLATE NOCASE DESC,job_id DESC);
         CREATE INDEX IF NOT EXISTS jobs_display_title ON local_jobs(connection_id,COALESCE(title,job_id) COLLATE NOCASE DESC,job_id DESC);
         CREATE INDEX IF NOT EXISTS jobs_display_printerName ON local_jobs(connection_id,COALESCE(printer_name,printer_id,'Not recorded') COLLATE NOCASE DESC,job_id DESC);
         CREATE INDEX IF NOT EXISTS jobs_display_status ON local_jobs(connection_id,(CASE COALESCE(state,'CLAIMED') WHEN 'READY' THEN 'Ready' WHEN 'OFFLINE' THEN 'Offline' WHEN 'PAUSED' THEN 'Paused' WHEN 'ERROR' THEN 'Needs attention' WHEN 'QUEUED' THEN 'Queued' WHEN 'CLAIMED' THEN 'Assigned to this computer' WHEN 'RECEIVED' THEN 'Received' WHEN 'SUBMITTING' THEN 'Sending to printer' WHEN 'SPOOLED' THEN 'Sent to printer' WHEN 'SPOOLER_COMPLETED' THEN 'Windows completed the job' WHEN 'FAILED' THEN 'Failed' WHEN 'OUTCOME_UNKNOWN' THEN 'Check printer before retrying' ELSE 'Status unavailable' END || CASE WHEN failure_acknowledged<>0 THEN ' · Reviewed' ELSE '' END) COLLATE NOCASE DESC,job_id DESC);
         CREATE INDEX IF NOT EXISTS jobs_display_format ON local_jobs(connection_id,COALESCE(format,'—') COLLATE NOCASE DESC,job_id DESC);
         """);
   }



   /// <summary>
   /// The remote-control tables and the three tables 1.x created only on first use: local names and descriptions,
   /// realtime settings per connection, and the certificate exception. An installation that never used one of them has
   /// none, so each is created here when missing; existing rows are untouched.
   /// </summary>
   private static void CreateControlAndSettings(JournalMigrationContext context)
   {
      context.Execute("""
         CREATE TABLE IF NOT EXISTS control_state(connection_id TEXT PRIMARY KEY,json TEXT NOT NULL,updated_at TEXT NOT NULL);
         CREATE TABLE IF NOT EXISTS control_commands(connection_id TEXT NOT NULL,command_id TEXT NOT NULL,command_json TEXT NOT NULL,receipt_json TEXT,acknowledged INTEGER NOT NULL DEFAULT 0,expires_at TEXT NOT NULL,PRIMARY KEY(connection_id,command_id));
         CREATE INDEX IF NOT EXISTS control_outbox ON control_commands(connection_id,acknowledged);
         CREATE INDEX IF NOT EXISTS control_expiry ON control_commands(expires_at);
         CREATE TABLE IF NOT EXISTS local_metadata(profile_key TEXT PRIMARY KEY,json TEXT NOT NULL);
         CREATE TABLE IF NOT EXISTS realtime_settings(connection_id TEXT PRIMARY KEY,json TEXT NOT NULL);
         CREATE TABLE IF NOT EXISTS server_trust(scope TEXT PRIMARY KEY,json TEXT NOT NULL);
         """);
   }
}
