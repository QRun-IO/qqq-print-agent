/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

public sealed class AgentStore : IJobJournal, IDisposable
{
   static AgentStore() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
   private readonly SqliteConnection _database;
   private readonly string _directory;

   public AgentStore(string directory)
   {
      _directory = Path.GetFullPath(directory);
      Directory.CreateDirectory(_directory);
      _database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "journal.db") }.ToString());
      _database.Open();
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
   }

   public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QqqPrintAgent");

   public void SaveConfiguration(AgentConfiguration config)
   {
      byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(config, PrintApiClient.JsonOptions);
      byte[] protectedData = ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
      string path = Path.Combine(_directory, "connection.dpapi");
      File.WriteAllBytes(path + ".tmp", protectedData);
      File.Move(path + ".tmp", path, overwrite: true);
      CryptographicOperations.ZeroMemory(plaintext);
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
      { return JsonSerializer.Deserialize<AgentConfiguration>(plaintext, PrintApiClient.JsonOptions); }
      finally { CryptographicOperations.ZeroMemory(plaintext); }
   }

   public string? GetState(string jobId)
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT state FROM events WHERE job_id=$job ORDER BY sequence DESC LIMIT 1";
      command.Parameters.AddWithValue("$job", jobId);
      return command.ExecuteScalar() as string;
   }

   public void Record(string jobId, PrintEvent printEvent)
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "INSERT INTO events(job_id,event_id,state,json,recorded_at) VALUES($job,$event,$state,$json,$time)";
      command.Parameters.AddWithValue("$job", jobId);
      command.Parameters.AddWithValue("$event", printEvent.EventId);
      command.Parameters.AddWithValue("$state", printEvent.Status);
      command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(printEvent, PrintApiClient.JsonOptions));
      command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
      command.ExecuteNonQuery();
   }

   public IReadOnlyList<(string JobId, PrintEvent Event)> Pending()
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT job_id,json FROM events WHERE acknowledged=0 ORDER BY sequence";
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
      command.CommandText = "UPDATE events SET acknowledged=1 WHERE event_id=$event";
      command.Parameters.AddWithValue("$event", eventId);
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
      if(exposed && !discovered.Any(printer => string.Equals(printer.PrinterId, printerId, StringComparison.Ordinal)))
      {
         throw new InvalidOperationException("Select a currently discovered printer.");
      }
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = exposed ? "INSERT OR IGNORE INTO exposed_printers(printer_id) VALUES($id)" : "DELETE FROM exposed_printers WHERE printer_id=$id";
      command.Parameters.AddWithValue("$id", printerId);
      command.ExecuteNonQuery();
   }



   /// <summary>Returns bounded latest job states without exposing document content.</summary>
   public IReadOnlyList<JobSnapshot> RecentJobs()
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT job_id,json,recorded_at FROM events WHERE sequence IN (SELECT MAX(sequence) FROM events GROUP BY job_id) ORDER BY sequence DESC LIMIT 50";
      using SqliteDataReader reader = command.ExecuteReader();
      List<JobSnapshot> jobs = [];
      while(reader.Read())
      {
         PrintEvent printEvent = JsonSerializer.Deserialize<PrintEvent>(reader.GetString(1), PrintApiClient.JsonOptions)!;
         jobs.Add(new JobSnapshot(reader.GetString(0), printEvent.Status, reader.IsDBNull(2) ? "Earlier session" : reader.GetString(2), printEvent.Message, printEvent.SpoolerJobId));
      }
      return jobs;
   }
}


