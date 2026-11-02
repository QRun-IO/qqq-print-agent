/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Creation-time migration of the job table. It exists so jobs saved before the column was added still sort by when they were created,
/// instead of by their last update.
/// </summary>
public sealed partial class AgentStore
{
   /// <summary>Adds the created-at column if missing, filling it from the first recorded event (or the update time), and ensures one sort index per display field. It runs in a single transaction so a failure leaves the old table unchanged.</summary>
   private void MigrateJobCreationTime()
   {
      using SqliteTransaction transaction = _database.BeginTransaction();
      using SqliteCommand command = _database.CreateCommand();
      command.Transaction = transaction;
      command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('local_jobs') WHERE name='created_at'";
      if(Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
      {
         command.CommandText = """
            ALTER TABLE local_jobs ADD COLUMN created_at TEXT;
            UPDATE local_jobs SET created_at=COALESCE(
               (SELECT recorded_at FROM events WHERE events.connection_id=local_jobs.connection_id
                AND events.job_id=local_jobs.job_id AND recorded_at IS NOT NULL ORDER BY sequence LIMIT 1),updated_at);
            """;
         command.ExecuteNonQuery();
      }
      foreach(string field in JobSorting.Fields)
      {
         command.CommandText = "CREATE INDEX IF NOT EXISTS jobs_display_" + field + " ON local_jobs(connection_id," + JobSorting.SqlColumn(field) + " COLLATE NOCASE DESC,job_id DESC)";
         command.ExecuteNonQuery();
      }
      transaction.Commit();
   }
}
