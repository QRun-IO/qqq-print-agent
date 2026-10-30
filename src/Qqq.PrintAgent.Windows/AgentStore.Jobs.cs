using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Qqq.PrintAgent.Windows;

public sealed partial class AgentStore
{
   private const string JobColumns = "job_id,state,updated_at,last_event_json,printer_id,printer_name,title,format,failure_acknowledged,history_pruned,created_at";

   public bool HasJob(string jobId)
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT EXISTS(SELECT 1 FROM local_jobs WHERE connection_id=$scope AND job_id=$job)";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.Parameters.AddWithValue("$job", jobId);
      return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
   }

   private void CreateJobIndexes()
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = """
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
         """;
      command.ExecuteNonQuery();
   }

   public JobPage QueryJobs(JobQuery query)
   {
      string order = ValidateJobQuery(query);
      using SqliteTransaction transaction = _database.BeginTransaction(deferred: true);
      using SqliteCommand command = _database.CreateCommand();
      command.Transaction = transaction;
      string where = "connection_id=$scope";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      if(!string.IsNullOrWhiteSpace(query.Search))
      {
         where += """
             AND job_id IN (
             SELECT job_id FROM local_jobs WHERE connection_id=$scope AND job_id LIKE $search ESCAPE '\'
             UNION SELECT job_id FROM local_jobs WHERE connection_id=$scope AND title LIKE $search ESCAPE '\'
             UNION SELECT job_id FROM local_jobs WHERE connection_id=$scope AND printer_name LIKE $search ESCAPE '\')
            """;
         string search = query.Search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
         command.Parameters.AddWithValue("$search", search + "%");
      }
      if(!string.IsNullOrWhiteSpace(query.Status))
      {
         where += " AND state=$state";
         command.Parameters.AddWithValue("$state", query.Status);
      }
      if(!string.IsNullOrWhiteSpace(query.PrinterId))
      {
         where += " AND printer_id=$printer";
         command.Parameters.AddWithValue("$printer", query.PrinterId);
      }
      if(query.NeedsAttentionOnly)
      {
         where += " AND state IN ('FAILED','OUTCOME_UNKNOWN') AND failure_acknowledged=0";
      }
      command.CommandText = "SELECT COUNT(*) FROM local_jobs WHERE " + where;
      int count = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
      command.CommandText = "SELECT " + JobColumns + " FROM local_jobs WHERE " + where + " ORDER BY " + order + " LIMIT $limit OFFSET $offset";
      command.Parameters.AddWithValue("$limit", query.PageSize);
      command.Parameters.AddWithValue("$offset", query.Offset);
      List<JobSnapshot> jobs = [];
      using(SqliteDataReader reader = command.ExecuteReader())
      {
         while(reader.Read())
         {
            jobs.Add(ReadJob(reader));
         }
      }
      transaction.Commit();
      return new JobPage(jobs, count, query.Offset, query.PageSize);
   }

   internal static string ValidateJobQuery(JobQuery query)
   {
      ArgumentNullException.ThrowIfNull(query);
      ArgumentOutOfRangeException.ThrowIfNegative(query.Offset, nameof(query));
      ArgumentOutOfRangeException.ThrowIfLessThan(query.PageSize, 1, nameof(query));
      ArgumentOutOfRangeException.ThrowIfGreaterThan(query.PageSize, 100, nameof(query));
      if(query.Search?.Length > 256 || query.Status?.Length > 32 || query.PrinterId?.Length > 256)
      {
         throw new ArgumentException("Job filter is too long.", nameof(query));
      }
      return JobSorting.SqlOrder(query);
   }

   /// <summary>One bounded maintenance batch; only delivered, reviewed terminal detail is removed.</summary>
   public int PruneHistory(int retainDays = 30, int maxDetailedJobs = 300000)
   {
      ArgumentOutOfRangeException.ThrowIfLessThan(retainDays, 1);
      ArgumentOutOfRangeException.ThrowIfLessThan(maxDetailedJobs, 1);
      using SqliteTransaction transaction = _database.BeginTransaction();
      using SqliteCommand command = _database.CreateCommand();
      command.Transaction = transaction;
      command.CommandText = """
         SELECT job_id FROM local_jobs j WHERE connection_id=$scope AND history_pruned=0
         AND state IN ('SPOOLED','SPOOLER_COMPLETED','FAILED')
         AND (state IN ('SPOOLED','SPOOLER_COMPLETED') OR failure_acknowledged=1)
         AND NOT EXISTS (SELECT 1 FROM events e WHERE e.connection_id=j.connection_id AND e.job_id=j.job_id AND acknowledged=0)
         AND (updated_at<$cutoff OR (updated_at,job_id)<=(SELECT updated_at,job_id FROM local_jobs
         WHERE connection_id=$scope AND history_pruned=0 ORDER BY updated_at DESC,job_id DESC LIMIT 1 OFFSET $limit))
         ORDER BY updated_at,job_id LIMIT 250
         """;
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-retainDays).ToString("O", CultureInfo.InvariantCulture));
      command.Parameters.AddWithValue("$limit", maxDetailedJobs);
      List<string> jobs = [];
      using(SqliteDataReader reader = command.ExecuteReader())
      {
         while(reader.Read())
         {
            jobs.Add(reader.GetString(0));
         }
      }
      command.CommandText = "DELETE FROM events WHERE connection_id=$scope AND job_id=$job; UPDATE local_jobs SET history_pruned=1 WHERE connection_id=$scope AND job_id=$job";
      SqliteParameter jobParameter = command.Parameters.Add("$job", SqliteType.Text);
      foreach(string jobId in jobs)
      {
         jobParameter.Value = jobId;
         command.ExecuteNonQuery();
      }
      transaction.Commit();
      return jobs.Count;
   }
}
