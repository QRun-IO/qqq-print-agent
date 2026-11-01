/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Qqq.PrintAgent.Windows;

public sealed partial class AgentStore
{
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
