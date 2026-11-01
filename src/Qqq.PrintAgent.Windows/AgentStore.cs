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

/// <summary>
/// The service's durable local state in one SQLite journal plus a DPAPI-protected connection file. It records every job
/// event before it is reported (so a crash never loses an outcome), keeps events and job summaries scoped to the server and
/// device they belong to, and holds the printer exposure choices. Other parts of this class add controls, metadata,
/// authentication, paging and retention. One instance owns one connection and is not shared across threads.
/// </summary>
public sealed partial class AgentStore : IJobJournal, IDisposable
{
   /// <summary>Selects the Windows system SQLite library once, so the agent ships no native SQLite binary of its own.</summary>
   static AgentStore() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());



   private readonly SqliteConnection _database;
   private readonly string _directory;
   private static readonly Lock SchemaLock = new();
   private string _connectionScope = "unenrolled";
   private bool _configurationRecoveryRequired;



   /// <summary>
   /// Opens (creating if needed) the journal under the protected state directory and brings its schema up to date. With
   /// recovery enabled, an unreadable saved connection no longer blocks opening: the store switches to an isolated scope so
   /// old evidence is kept but never attached to a replacement identity. On any failure the connection is closed.
   /// </summary>
   public AgentStore(string directory, bool recoverUnreadableConfiguration = false)
   {
      _directory = Path.GetFullPath(directory);
      Qqq.PrintAgent.Core.MachineStateDirectory.EnsureForPath(_directory);
      Directory.CreateDirectory(_directory);
      _database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "journal.db") }.ToString());
      try
      {
         _database.Open();
         InitializeDatabase(recoverUnreadableConfiguration);
      }
      catch
      {
         _database.Dispose();
         throw;
      }
   }



   /// <summary>
   /// Creates or migrates the schema under a process-wide lock: events, exposure, per-job summaries, and the added
   /// timestamp and scope columns, with legacy rows assigned to the current scope. Job summaries are rebuilt from the latest
   /// event only the first time that table appears. The migration runs in a transaction so a failure leaves the old schema usable.
   /// </summary>
   private void InitializeDatabase(bool recoverUnreadableConfiguration)
   {
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
         command.CommandText = "UPDATE events SET connection_id=$scope WHERE connection_id IS NULL";
         command.Parameters.AddWithValue("$scope", _connectionScope);
         command.ExecuteNonQuery();
         command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='local_jobs'";
         bool migrateJobs = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0;
         using SqliteTransaction migration = _database.BeginTransaction();
         command.Transaction = migration;
         command.CommandText = "CREATE TABLE IF NOT EXISTS local_jobs(connection_id TEXT NOT NULL, job_id TEXT NOT NULL, printer_id TEXT, printer_name TEXT, title TEXT, format TEXT, state TEXT, last_event_json TEXT, updated_at TEXT, failure_acknowledged INTEGER NOT NULL DEFAULT 0, history_pruned INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(connection_id,job_id));";
         command.ExecuteNonQuery();
         if(migrateJobs)
         {
            command.CommandText = "INSERT OR IGNORE INTO local_jobs(connection_id,job_id,state,last_event_json,updated_at) SELECT connection_id,job_id,state,json,recorded_at FROM events WHERE sequence IN (SELECT MAX(sequence) FROM events GROUP BY connection_id,job_id);";
            command.ExecuteNonQuery();
         }
         migration.Commit();
         command.Transaction = null;
         CreateJobIndexes();
         MigrateJobCreationTime();
         InitializeControls();
      }
   }



   /// <summary>The machine-wide state directory under ProgramData that the service uses; the companion never writes here directly.</summary>
   public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QqqPrintAgent");



   /// <summary>
   /// Saves the device connection protected for the service account with DPAPI, writing a temporary file and moving it into
   /// place so a crash cannot leave a half-written secret. When recovery was needed, the unreadable file is copied aside
   /// first, so nothing is destroyed. The plaintext buffer is zeroed on every path.
   /// </summary>
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



   /// <summary>
   /// Reads and decrypts the saved connection, or returns null when none exists or recovery is pending. Loading also sets
   /// the connection scope that every later event and job query uses, so history can never cross to another server or
   /// device. A file that cannot be decrypted throws; the caller decides whether recovery is allowed.
   /// </summary>
   public AgentConfiguration? LoadConfiguration()
   {
      if(_configurationRecoveryRequired)
      {
         return null;
      }
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



   /// <summary>The last recorded state of a job on this connection, or null when it is unknown; the worker uses it to refuse reprinting a finished job.</summary>
   public string? GetState(string jobId)
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT state FROM local_jobs WHERE connection_id=$scope AND job_id=$job";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.Parameters.AddWithValue("$job", jobId);
      return command.ExecuteScalar() as string;
   }



   /// <summary>
   /// Appends an event and updates the job summary in one transaction, so the delivery outbox and the visible state never
   /// disagree. A new state clears any earlier operator review and the history-pruned mark, and the first event fixes the job's creation time.
   /// </summary>
   public void Record(string jobId, PrintEvent printEvent)
   {
      using SqliteTransaction transaction = _database.BeginTransaction();
      using SqliteCommand command = _database.CreateCommand();
      command.Transaction = transaction;
      command.CommandText = "INSERT INTO events(job_id,event_id,state,json,recorded_at,connection_id) VALUES($job,$event,$state,$json,$time,$scope); INSERT INTO local_jobs(connection_id,job_id,state,last_event_json,updated_at,created_at) VALUES($scope,$job,$state,$json,$time,$time) ON CONFLICT(connection_id,job_id) DO UPDATE SET state=$state,last_event_json=$json,updated_at=$time,created_at=COALESCE(local_jobs.created_at,$time),failure_acknowledged=0,history_pruned=0;";
      command.Parameters.AddWithValue("$job", jobId);
      command.Parameters.AddWithValue("$event", printEvent.EventId);
      command.Parameters.AddWithValue("$state", printEvent.Status);
      command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(printEvent, PrintApiClient.JsonOptions));
      command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.ExecuteNonQuery();
      transaction.Commit();
   }



   /// <summary>
   /// Stores the job's display facts (printer, bounded title, format) when it is claimed, so history stays readable even
   /// after the printer or server record changes. The server's creation time is kept once and never overwritten later.
   /// </summary>
   public void TrackJob(PrintJob job, string? printerName)
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "INSERT INTO local_jobs(connection_id,job_id,printer_id,printer_name,title,format,created_at) VALUES($scope,$job,$printer,$name,$title,$format,$created) ON CONFLICT(connection_id,job_id) DO UPDATE SET printer_id=$printer,printer_name=$name,title=$title,format=$format,created_at=COALESCE(local_jobs.created_at,$created);";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.Parameters.AddWithValue("$job", job.JobId);
      command.Parameters.AddWithValue("$printer", job.PrinterId);
      command.Parameters.AddWithValue("$name", (object?)Bounded(printerName) ?? DBNull.Value);
      command.Parameters.AddWithValue("$title", (object?)Bounded(job.Title) ?? DBNull.Value);
      command.Parameters.AddWithValue("$format", job.Format);
      command.Parameters.AddWithValue("$created", JobSorting.NormalizeCreatedAt(job.CreatedAt) ?? DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
      command.ExecuteNonQuery();
   }



   /// <summary>The next batch (at most 200, oldest first) of events not yet acknowledged by the server, so an outage is drained in order and in bounded steps.</summary>
   public IReadOnlyList<(string JobId, PrintEvent Event)> Pending()
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT job_id,json FROM events WHERE connection_id=$scope AND acknowledged=0 ORDER BY sequence LIMIT 200";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      using SqliteDataReader reader = command.ExecuteReader();
      List<(string, PrintEvent)> events = [];
      while(reader.Read())
      {
         events.Add((reader.GetString(0), JsonSerializer.Deserialize<PrintEvent>(reader.GetString(1), PrintApiClient.JsonOptions)!));
      }

      return events;
   }



   /// <summary>Marks one event delivered. Called only after the server accepted it, so a failed delivery stays pending and is retried.</summary>
   public void Acknowledge(string eventId)
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "UPDATE events SET acknowledged=1 WHERE connection_id=$scope AND event_id=$event";
      command.Parameters.AddWithValue("$event", eventId);
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.ExecuteNonQuery();
   }



   /// <summary>Closes the journal connection; stores are short-lived and opened per unit of work rather than held open.</summary>
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
      command.CommandText = "SELECT " + JobColumns + " FROM local_jobs WHERE connection_id=$scope ORDER BY CASE WHEN state IN ('FAILED','OUTCOME_UNKNOWN') AND failure_acknowledged=0 THEN 0 ELSE 1 END,updated_at DESC,job_id DESC LIMIT 50";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      using SqliteDataReader reader = command.ExecuteReader();
      List<JobSnapshot> jobs = [];
      while(reader.Read())
      {
         jobs.Add(ReadJob(reader));
      }
      return jobs;
   }



   /// <summary>
   /// Returns one job with up to its latest 200 events in time order, or null when the job is not on this connection. It
   /// also reports whether older detail was pruned by retention, so the window can say that history was trimmed rather than missing.
   /// </summary>
   public JobDetailsSnapshot? GetJobDetails(string jobId)
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT " + JobColumns + " FROM local_jobs WHERE connection_id=$scope AND job_id=$job";
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
      command.CommandText = "SELECT json,recorded_at,acknowledged FROM events WHERE connection_id=$scope AND job_id=$job ORDER BY sequence DESC LIMIT 200";
      using SqliteDataReader eventsReader = command.ExecuteReader();
      List<JobEventSnapshot> events = [];
      while(eventsReader.Read())
      {
         PrintEvent item = JsonSerializer.Deserialize<PrintEvent>(eventsReader.GetString(0), PrintApiClient.JsonOptions)!;
         events.Add(new JobEventSnapshot(item.EventId, item.Status, eventsReader.IsDBNull(1) ? "Earlier session" : eventsReader.GetString(1), item.Message, item.SpoolerJobId, eventsReader.GetInt32(2) != 0));
      }
      events.Reverse();
      return new JobDetailsSnapshot(job, events, pruned);
   }



   /// <summary>
   /// Records the operator's review of a failed or uncertain job. Review is independent from delivery of status to QQQ and
   /// never changes the printing result; only FAILED and OUTCOME_UNKNOWN jobs of this connection can be reviewed.
   /// </summary>
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



   /// <summary>Counts failed or uncertain jobs the operator has not reviewed; it drives the health color so an old failure is not hidden by newer successes.</summary>
   public int NeedsAttentionCount()
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT COUNT(*) FROM local_jobs WHERE connection_id=$scope AND state IN ('FAILED','OUTCOME_UNKNOWN') AND failure_acknowledged=0";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
   }



   /// <summary>Builds a job summary from the standard job column order; a missing state reads as CLAIMED and a missing time as an earlier session.</summary>
   private static JobSnapshot ReadJob(SqliteDataReader reader)
   {
      PrintEvent? item = reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<PrintEvent>(reader.GetString(3), PrintApiClient.JsonOptions);
      return new JobSnapshot(reader.GetString(0), reader.IsDBNull(1) ? "CLAIMED" : reader.GetString(1), reader.IsDBNull(2) ? "Earlier session" : reader.GetString(2), item?.Message, item?.SpoolerJobId,
         NullableString(reader, 4), NullableString(reader, 5), NullableString(reader, 6), NullableString(reader, 7), reader.GetInt32(8) != 0, NullableString(reader, 10));
   }



   /// <summary>Reads a text column that may be NULL.</summary>
   private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);



   /// <summary>Caps stored text at 1024 characters so an oversized title or name from the server cannot bloat the journal.</summary>
   private static string? Bounded(string? text) => text is null ? null : text[..Math.Min(text.Length, 1024)];



   /// <summary>Latest unfinished submissions for this connection, including acknowledged states.</summary>
   public IReadOnlyList<(string JobId, string State)> Incomplete()
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT job_id,state FROM local_jobs WHERE connection_id=$scope AND state IN ('RECEIVED','SUBMITTING') LIMIT 200";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      using SqliteDataReader reader = command.ExecuteReader();
      List<(string, string)> incomplete = [];
      while(reader.Read())
      {
         incomplete.Add((reader.GetString(0), reader.GetString(1)));
      }
      return incomplete;
   }



   /// <summary>
   /// Derives the scope key from the normalized server address and device identifier. Hashing both keeps each server and
   /// device's history separate on disk, so re-enrolling elsewhere cannot send old events under the new credential.
   /// </summary>
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


