/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>The job list sorts by when the server created each job. These tests pin how that creation time is stored: normalized to UTC, kept stable when a job is replayed or changes status, and filled in for journals written before the column existed.</summary>
public sealed class JobCreationTimeTests
{
   /// <summary>A server creation time with an offset is stored as UTC and never replaced by a later claim carrying a different time, nor by status updates (which change the updated time only). Sorting by creation then orders the jobs by their server creation times in both directions.</summary>
   [Fact]
   public void Receipt_NormalizesServerCreationAndPreservesItAcrossStatusChangesAndReplay()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-created-" + Guid.NewGuid().ToString("N"));
      try
      {
         using AgentStore store = new(directory);
         PrintJob older = new("older", "printer", "Older job", "PDF", "00", 1, new PrintOptions(), "2026-01-01T05:00:00-05:00", "CLAIMED");
         store.TrackJob(older, "Laser");
         store.Record(older.JobId, new PrintEvent("received", "RECEIVED"));
         store.TrackJob(older with { CreatedAt = "2099-01-01T00:00:00Z" }, "Laser");
         PrintJob newer = older with { JobId = "newer", CreatedAt = "2026-01-02T10:00:00Z" };
         store.TrackJob(newer, "Laser");
         store.Record(newer.JobId, new PrintEvent("newer-spooled", "SPOOLED"));
         store.Record(older.JobId, new PrintEvent("older-spooled", "SPOOLED"));
         JobSnapshot saved = store.GetJobDetails(older.JobId)!.Job;
         Assert.Equal("2026-01-01T10:00:00.0000000+00:00", saved.CreatedAt);
         Assert.NotEqual(saved.CreatedAt, saved.UpdatedAt, StringComparer.Ordinal);
         Assert.Equal(new[] { "newer", "older" }, store.QueryJobs(new JobQuery()).Jobs.Select(job => job.JobId), StringComparer.Ordinal);
         Assert.Equal(new[] { "older", "newer" }, store.QueryJobs(new JobQuery(Descending: false)).Jobs.Select(job => job.JobId), StringComparer.Ordinal);
      }
      finally
      {
         SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>
   /// For a journal created before the creation-time column existed, opening the store fills it from the job's first event, or from
   /// the last-updated time when its events were pruned. The fill happens once: changing every timestamp afterwards and reopening
   /// leaves the earlier values alone, so the migration is not repeated on each open.
   /// </summary>
   [Fact]
   public void LegacyBackfill_UsesFirstEventThenSummaryFallbackAndRunsOnlyOnce()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-created-migration-" + Guid.NewGuid().ToString("N"));
      try
      {
         using(AgentStore store = new(directory))
         {
            store.Record("events", new PrintEvent("first", "RECEIVED"));
            store.Record("events", new PrintEvent("last", "SPOOLED"));
            store.Record("pruned", new PrintEvent("pruned", "SPOOLED"));
         }
         Execute(directory, """
            UPDATE events SET recorded_at='2020-01-01T00:00:00.0000000+00:00' WHERE event_id='first';
            UPDATE events SET recorded_at='2020-02-01T00:00:00.0000000+00:00' WHERE event_id='last';
            DELETE FROM events WHERE job_id='pruned';
            UPDATE local_jobs SET updated_at='2021-01-01T00:00:00.0000000+00:00' WHERE job_id='pruned';
            DROP INDEX jobs_display_createdAt;
            ALTER TABLE local_jobs DROP COLUMN created_at;
            """);
         using(AgentStore migrated = new(directory))
         {
            Assert.Equal("2020-01-01T00:00:00.0000000+00:00", migrated.GetJobDetails("events")!.Job.CreatedAt);
            Assert.Equal("2021-01-01T00:00:00.0000000+00:00", migrated.GetJobDetails("pruned")!.Job.CreatedAt);
            migrated.Record("events", new PrintEvent("late", "SPOOLER_COMPLETED"));
         }
         Execute(directory, "UPDATE events SET recorded_at='2099-01-01T00:00:00.0000000+00:00'; UPDATE local_jobs SET updated_at='2099-01-01T00:00:00.0000000+00:00'");
         using AgentStore reopened = new(directory);
         Assert.Equal("2020-01-01T00:00:00.0000000+00:00", reopened.GetJobDetails("events")!.Job.CreatedAt);
         Assert.Equal("2021-01-01T00:00:00.0000000+00:00", reopened.GetJobDetails("pruned")!.Job.CreatedAt);
      }
      finally
      {
         SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>Runs raw SQL against the journal file directly, bypassing the store, to simulate an older schema or tampered timestamps.</summary>
   private static void Execute(string directory, string sql)
   {
      using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "journal.db") }.ToString());
      connection.Open();
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = sql;
      command.ExecuteNonQuery();
   }
}
