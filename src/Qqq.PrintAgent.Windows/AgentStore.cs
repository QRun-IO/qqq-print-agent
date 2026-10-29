/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

public sealed class AgentStore : IJobJournal, IDisposable
{
   static AgentStore() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
   private readonly SqliteConnection _database;
   private readonly string _directory;
   private static readonly Lock SchemaLock = new();
   private string _connectionScope = "unenrolled";
   private bool _configurationRecoveryRequired;

   public AgentStore(string directory, bool recoverUnreadableConfiguration = false)
   {
      _directory = Path.GetFullPath(directory);
      Directory.CreateDirectory(_directory);
      _database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "journal.db") }.ToString());
      _database.Open();
      lock(SchemaLock)
      {
         using SqliteCommand command = _database.CreateCommand();
         command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS events(sequence INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL, event_id TEXT NOT NULL UNIQUE, state TEXT NOT NULL, json TEXT NOT NULL, acknowledged INTEGER NOT NULL DEFAULT 0);";
         command.ExecuteNonQuery();
         command.CommandText = "CREATE TABLE IF NOT EXISTS exposed_printers(printer_id TEXT PRIMARY KEY); SELECT COUNT(*) FROM pragma_table_info('events') WHERE name='recorded_at';";
         long hasTimestamp = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
         if(hasTimestamp == 0)
         {
            command.CommandText = "ALTER TABLE events ADD COLUMN recorded_at TEXT";
            command.ExecuteNonQuery();
         }
         command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('events') WHERE name='connection_id'";
         if(Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
         {
            command.CommandText = "ALTER TABLE events ADD COLUMN connection_id TEXT";
            command.ExecuteNonQuery();
         }
         try
         {
            _ = LoadConfiguration();
         }
         catch(Exception exception) when(recoverUnreadableConfiguration && exception is CryptographicException or JsonException or ArgumentException or UriFormatException)
         {
            // Only explicit re-enrollment may replace unreadable credentials. Never attach
            // unscoped legacy evidence to the new identity when its original scope is unknown.
            _configurationRecoveryRequired = true;
            _connectionScope = "unreadable-" + Guid.NewGuid().ToString("N");
         }
         catch
         {
            _database.Dispose();
            throw;
         }
         command.CommandText = "UPDATE events SET connection_id=$scope WHERE connection_id IS NULL";
         command.Parameters.AddWithValue("$scope", _connectionScope);
         command.ExecuteNonQuery();
         command.CommandText = "CREATE TABLE IF NOT EXISTS local_jobs(connection_id TEXT NOT NULL, job_id TEXT NOT NULL, printer_id TEXT, printer_name TEXT, title TEXT, format TEXT, state TEXT, last_event_json TEXT, updated_at TEXT, failure_acknowledged INTEGER NOT NULL DEFAULT 0, history_pruned INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(connection_id,job_id)); INSERT OR IGNORE INTO local_jobs(connection_id,job_id,state,last_event_json,updated_at) SELECT connection_id,job_id,state,json,recorded_at FROM events WHERE sequence IN (SELECT MAX(sequence) FROM events GROUP BY connection_id,job_id);";
         command.ExecuteNonQuery();
      }
   }

   public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QqqPrintAgent");

   public void SaveConfiguration(AgentConfiguration config)
   {
      byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(config, PrintApiClient.JsonOptions);
      try
      {
         string scope = ConnectionScope(config);
         byte[] protectedData = ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
         string path = Path.Combine(_directory, "connection.dpapi");
         if(_configurationRecoveryRequired)
         {
            File.Copy(path, Path.Combine(_directory, "connection.recovery-" + Guid.NewGuid().ToString("N") + ".dpapi"));
         }
         File.WriteAllBytes(path + ".tmp", protectedData);
         File.Move(path + ".tmp", path, overwrite: true);
         _connectionScope = scope;
         _configurationRecoveryRequired = false;
      }
      finally { CryptographicOperations.ZeroMemory(plaintext); }
   }

   public AgentConfiguration? LoadConfiguration()
   {
      string path = Path.Combine(_directory, "connection.dpapi");
      if(!File.Exists(path))
      {
         return null;
      }

      byte[] plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), optionalEntropy: null, DataProtectionScope.CurrentUser);
      try
      {
         AgentConfiguration? configuration = JsonSerializer.Deserialize<AgentConfiguration>(plaintext, PrintApiClient.JsonOptions);
         _connectionScope = ConnectionScope(configuration);
         return configuration;
      }
      finally { CryptographicOperations.ZeroMemory(plaintext); }
   }

   public string? GetState(string jobId)
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT state FROM local_jobs WHERE connection_id=$scope AND job_id=$job";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.Parameters.AddWithValue("$job", jobId);
      return command.ExecuteScalar() as string;
   }

   public void Record(string jobId, PrintEvent printEvent)
   {
      using SqliteTransaction transaction = _database.BeginTransaction();
      using SqliteCommand command = _database.CreateCommand();
      command.Transaction = transaction;
      command.CommandText = "INSERT INTO events(job_id,event_id,state,json,recorded_at,connection_id) VALUES($job,$event,$state,$json,$time,$scope); INSERT INTO local_jobs(connection_id,job_id,state,last_event_json,updated_at) VALUES($scope,$job,$state,$json,$time) ON CONFLICT(connection_id,job_id) DO UPDATE SET state=$state,last_event_json=$json,updated_at=$time,failure_acknowledged=0,history_pruned=0;";
      command.Parameters.AddWithValue("$job", jobId);
      command.Parameters.AddWithValue("$event", printEvent.EventId);
      command.Parameters.AddWithValue("$state", printEvent.Status);
      command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(printEvent, PrintApiClient.JsonOptions));
      command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.ExecuteNonQuery();
      transaction.Commit();
   }

   public void TrackJob(PrintJob job, string? printerName)
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "INSERT INTO local_jobs(connection_id,job_id,printer_id,printer_name,title,format) VALUES($scope,$job,$printer,$name,$title,$format) ON CONFLICT(connection_id,job_id) DO UPDATE SET printer_id=$printer,printer_name=$name,title=$title,format=$format;";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.Parameters.AddWithValue("$job", job.JobId);
      command.Parameters.AddWithValue("$printer", job.PrinterId);
      command.Parameters.AddWithValue("$name", (object?)Bounded(printerName) ?? DBNull.Value);
      command.Parameters.AddWithValue("$title", (object?)Bounded(job.Title) ?? DBNull.Value);
      command.Parameters.AddWithValue("$format", job.Format);
      command.ExecuteNonQuery();
   }

   public IReadOnlyList<(string JobId, PrintEvent Event)> Pending()
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT job_id,json FROM events WHERE connection_id=$scope AND acknowledged=0 ORDER BY sequence";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      using SqliteDataReader reader = command.ExecuteReader();
      List<(string, PrintEvent)> events = [];
      while(reader.Read())
      {
         events.Add((reader.GetString(0), JsonSerializer.Deserialize<PrintEvent>(reader.GetString(1), PrintApiClient.JsonOptions)!));
      }

      return events;
   }

   public void Acknowledge(string eventId)
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "UPDATE events SET acknowledged=1 WHERE connection_id=$scope AND event_id=$event";
      command.Parameters.AddWithValue("$event", eventId);
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.ExecuteNonQuery();
   }

   public void Dispose() => _database.Dispose();



   /// <summary>Queues explicitly selected on this computer. New installations expose none.</summary>
   public IReadOnlySet<string> GetExposedPrinterIds()
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT printer_id FROM exposed_printers";
      using SqliteDataReader reader = command.ExecuteReader();
      HashSet<string> result = new(StringComparer.Ordinal);
      while(reader.Read())
      {
         result.Add(reader.GetString(0));
      }
      return result;
   }



   /// <summary>Persists a local exposure choice after validating it against current discovery.</summary>
   public void SetExposure(string printerId, bool exposed, IReadOnlyList<PrinterInfo> discovered)
   {
      if(exposed && !discovered.Any(printer => string.Equals(printer.PrinterId, printerId, StringComparison.Ordinal) && printer.Formats.Length > 0))
      {
         throw new InvalidOperationException("Select a currently discovered printer that supports unattended printing.");
      }
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = exposed ? "INSERT OR IGNORE INTO exposed_printers(printer_id) VALUES($id)" : "DELETE FROM exposed_printers WHERE printer_id=$id";
      command.Parameters.AddWithValue("$id", printerId);
      command.ExecuteNonQuery();
   }



   /// <summary>Returns bounded job states, prioritizing unresolved failures before recent activity.</summary>
   public IReadOnlyList<JobSnapshot> RecentJobs()
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT job_id,state,updated_at,last_event_json,printer_id,printer_name,title,format,failure_acknowledged,history_pruned FROM local_jobs WHERE connection_id=$scope ORDER BY CASE WHEN state IN ('FAILED','OUTCOME_UNKNOWN') AND failure_acknowledged=0 THEN 0 ELSE 1 END,updated_at DESC LIMIT 50";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      using SqliteDataReader reader = command.ExecuteReader();
      List<JobSnapshot> jobs = [];
      while(reader.Read())
      {
         jobs.Add(ReadJob(reader));
      }
      return jobs;
   }

   public JobDetailsSnapshot? GetJobDetails(string jobId)
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT job_id,state,updated_at,last_event_json,printer_id,printer_name,title,format,failure_acknowledged,history_pruned FROM local_jobs WHERE connection_id=$scope AND job_id=$job";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.Parameters.AddWithValue("$job", jobId);
      JobSnapshot job;
      bool pruned;
      using(SqliteDataReader reader = command.ExecuteReader())
      {
         if(!reader.Read())
         {
            return null;
         }
         job = ReadJob(reader);
         pruned = reader.GetInt32(9) != 0;
      }
      command.CommandText = "SELECT json,recorded_at,acknowledged FROM events WHERE connection_id=$scope AND job_id=$job ORDER BY sequence";
      using SqliteDataReader eventsReader = command.ExecuteReader();
      List<JobEventSnapshot> events = [];
      while(eventsReader.Read())
      {
         PrintEvent item = JsonSerializer.Deserialize<PrintEvent>(eventsReader.GetString(0), PrintApiClient.JsonOptions)!;
         events.Add(new JobEventSnapshot(item.EventId, item.Status, eventsReader.IsDBNull(1) ? "Earlier session" : eventsReader.GetString(1), item.Message, item.SpoolerJobId, eventsReader.GetInt32(2) != 0));
      }
      return new JobDetailsSnapshot(job, events, pruned);
   }

   /// <summary>Operator review is independent from successful delivery of status to QQQ.</summary>
   public void AcknowledgeFailure(string jobId)
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "UPDATE local_jobs SET failure_acknowledged=1 WHERE connection_id=$scope AND job_id=$job AND state IN ('FAILED','OUTCOME_UNKNOWN')";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.Parameters.AddWithValue("$job", jobId);
      if(command.ExecuteNonQuery() == 0)
      {
         throw new InvalidOperationException("Select a failed or uncertain job from this connection.");
      }
   }

   public int NeedsAttentionCount()
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT COUNT(*) FROM local_jobs WHERE connection_id=$scope AND state IN ('FAILED','OUTCOME_UNKNOWN') AND failure_acknowledged=0";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
   }

   /// <summary>Prune delivered, reviewed terminal event detail while retaining deduplication state.</summary>
   public int PruneHistory(int retainDays = 30, int maxDetailedJobs = 1000)
   {
      ArgumentOutOfRangeException.ThrowIfLessThan(retainDays, 1);
      ArgumentOutOfRangeException.ThrowIfLessThan(maxDetailedJobs, 1);
      using SqliteTransaction transaction = _database.BeginTransaction();
      using SqliteCommand command = _database.CreateCommand();
      command.Transaction = transaction;
      command.CommandText = "SELECT job_id FROM local_jobs j WHERE connection_id=$scope AND history_pruned=0 AND state IN ('SPOOLED','SPOOLER_COMPLETED','FAILED','OUTCOME_UNKNOWN') AND (state IN ('SPOOLED','SPOOLER_COMPLETED') OR failure_acknowledged=1) AND NOT EXISTS (SELECT 1 FROM events e WHERE e.connection_id=j.connection_id AND e.job_id=j.job_id AND acknowledged=0) AND (updated_at<$cutoff OR job_id IN (SELECT job_id FROM local_jobs WHERE connection_id=$scope AND history_pruned=0 ORDER BY updated_at DESC LIMIT -1 OFFSET $limit))";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-retainDays).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
      command.Parameters.AddWithValue("$limit", maxDetailedJobs);
      List<string> jobs = [];
      using(SqliteDataReader reader = command.ExecuteReader())
      {
         while(reader.Read())
         {
            jobs.Add(reader.GetString(0));
         }
      }
      foreach(string jobId in jobs)
      {
         command.CommandText = "DELETE FROM events WHERE connection_id=$scope AND job_id=$job; UPDATE local_jobs SET history_pruned=1 WHERE connection_id=$scope AND job_id=$job";
         command.Parameters.AddWithValue("$job", jobId);
         command.ExecuteNonQuery();
         command.Parameters.RemoveAt("$job");
      }
      transaction.Commit();
      return jobs.Count;
   }

   private static JobSnapshot ReadJob(SqliteDataReader reader)
   {
      PrintEvent? item = reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<PrintEvent>(reader.GetString(3), PrintApiClient.JsonOptions);
      return new JobSnapshot(reader.GetString(0), reader.IsDBNull(1) ? "CLAIMED" : reader.GetString(1), reader.IsDBNull(2) ? "Earlier session" : reader.GetString(2), item?.Message, item?.SpoolerJobId,
         NullableString(reader, 4), NullableString(reader, 5), NullableString(reader, 6), NullableString(reader, 7), reader.GetInt32(8) != 0);
   }

   private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

   private static string? Bounded(string? text) => text is null ? null : text[..Math.Min(text.Length, 256)];

   /// <summary>Latest unfinished submissions for this connection, including acknowledged states.</summary>
   public IReadOnlyList<(string JobId, string State)> Incomplete()
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT job_id,state FROM local_jobs WHERE connection_id=$scope AND state IN ('RECEIVED','SUBMITTING')";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      using SqliteDataReader reader = command.ExecuteReader();
      List<(string, string)> incomplete = [];
      while(reader.Read())
      {
         incomplete.Add((reader.GetString(0), reader.GetString(1)));
      }
      return incomplete;
   }

   private static string ConnectionScope(AgentConfiguration? configuration)
   {
      if(configuration is null)
      {
         return "unenrolled";
      }
      string address = new Uri(configuration.ServerUrl).AbsoluteUri.TrimEnd('/');
      return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address + "\n" + configuration.DeviceId)));
   }

}


