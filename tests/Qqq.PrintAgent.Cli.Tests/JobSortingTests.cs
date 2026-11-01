/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The job history list is sorted and paged by the database, then merged with the server's bounded queue in memory. These tests
/// hold the two orderings to one definition so a page boundary never repeats, drops or reorders a job.
/// </summary>
public sealed class JobSortingTests
{
   /// <summary>
   /// Seeds 10,000 local jobs plus a 50-entry cloud queue (one entry duplicating a local job) and checks every sortable column in
   /// both directions at the first page, a page spanning the local/cloud boundary and a page past the end. It also proves that an
   /// unknown sort column is rejected instead of reaching SQL, and that a page read never parses distant rows: a corrupt payload
   /// far from the page would fail the call if the whole history were materialized.
   /// </summary>
   [Fact]
   public void TenThousandJobs_AllColumnsSortAcrossPagesAndInterleaveBoundedCloudQueue()
   {
      using Fixture fixture = new();
      JobSnapshot[] local = fixture.Seed(10000);
      JobSnapshot[] cloud = Enumerable.Range(0, 49).Select(index => MakeJob(index * 211, "cloud" + index.ToString("D4", CultureInfo.InvariantCulture))).Append(local[10] with { Status = "QUEUED" }).ToArray();
      foreach(string field in new[] { "createdAt", "title", "printerName", "status", "format" })
      {
         foreach(bool descending in new[] { true, false })
         {
            VerifyPages(fixture.Store, local, cloud, new JobQuery(SortBy: field, Descending: descending));
         }
      }
      Assert.Throws<ArgumentException>(() => fixture.Store.QueryJobs(new JobQuery(SortBy: "title; DELETE FROM local_jobs")));
      // A distant corrupt payload would expose any accidental whole-history materialization.
      fixture.Execute("UPDATE local_jobs SET last_event_json='invalid-json' WHERE job_id='job09999'");
      Assert.Equal(100, AgentRuntime.QueryJobsWithQueue(fixture.Store, new JobQuery(Offset: 5000, PageSize: 100), cloud).Jobs.Count);
   }



   /// <summary>
   /// Compares database pages and merged database-plus-queue pages with an independent in-memory sort of the same jobs. The merged
   /// total must exclude queue entries already stored locally and must report that the server queue was limited.
   /// </summary>
   private static void VerifyPages(AgentStore store, JobSnapshot[] local, JobSnapshot[] cloud, JobQuery query)
   {
      var comparer = Comparer<JobSnapshot>.Create((left, right) => JobSorting.Compare(left, right, query));
      JobSnapshot[] expectedLocal = local.Order(comparer).ToArray();
      JobSnapshot[] expectedAll = local.Concat(cloud.Where(job => !local.Any(saved => string.Equals(saved.JobId, job.JobId, StringComparison.Ordinal)))).Order(comparer).ToArray();
      foreach(int offset in new[] { 0, 4990, 10020 })
      {
         JobQuery pageQuery = query with { Offset = offset, PageSize = 100 };
         JobPage page = store.QueryJobs(pageQuery);
         Assert.Equal(expectedLocal.Skip(offset).Take(100).Select(job => job.JobId), page.Jobs.Select(job => job.JobId), StringComparer.Ordinal);
         JobPage merged = AgentRuntime.QueryJobsWithQueue(store, pageQuery, cloud);
         Assert.Equal(expectedAll.Skip(offset).Take(100).Select(job => job.JobId), merged.Jobs.Select(job => job.JobId), StringComparer.Ordinal);
         Assert.Equal(expectedAll.Length, merged.TotalCount);
         Assert.True(merged.ServerQueueLimited);
      }
   }



   /// <summary>
   /// The in-memory comparator used for queued jobs must order text exactly as SQLite orders the stored jobs, including nulls,
   /// case, accents, private-use and astral characters and embedded NULs. Any difference would make a job jump position when it
   /// moves from the server queue into local history.
   /// </summary>
   [Fact]
   public void QueueTextComparator_MatchesSqliteUnicodeNullAndCaseOrdering()
   {
      using Fixture fixture = new();
      string?[] values = [null, "", "alpha", "ALPHA", "Éclair", "éclair", "\uE000", "😀", "\0a", "\0bb"];
      foreach(string? first in values)
      {
         foreach(string? second in values)
         {
            int expected = fixture.CompareSql(first, second);
            JobSnapshot left = new("same", "QUEUED", "", null, null, Title: first, CreatedAt: first);
            JobSnapshot right = left with { CreatedAt = second };
            Assert.Equal(expected, Math.Sign(JobSorting.Compare(left, right, new JobQuery(Descending: false))));
         }
      }
      JobSnapshot lowerId = new("\uE000", "QUEUED", "", null, null);
      JobSnapshot higherId = lowerId with { JobId = "😀" };
      Assert.True(JobSorting.Compare(lowerId, higherId, new JobQuery(Descending: false)) < 0);
   }



   /// <summary>
   /// Builds a deterministic job whose fields vary by index (missing created time, printer, title or format, and every status),
   /// so sorting is exercised on nulls and ties rather than on tidy data.
   /// </summary>
   private static JobSnapshot MakeJob(int index, string id)
   {
      string?[] titles = [null, "alpha", "ALPHA", "Éclair", "éclair", "\uE000", "😀", "Packing slip"];
      string[] states = ["QUEUED", "CLAIMED", "RECEIVED", "SUBMITTING", "SPOOLED", "SPOOLER_COMPLETED", "FAILED", "OUTCOME_UNKNOWN"];
      string? created = index % 17 == 0 ? null : new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(index).ToString("O", CultureInfo.InvariantCulture);
      string updated = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(-index).ToString("O", CultureInfo.InvariantCulture);
      return new JobSnapshot(id, states[index % states.Length], updated, null, null, index % 13 == 0 ? null : "printer-id",
         titles[(index / 3) % titles.Length], titles[index % titles.Length], index % 5 == 0 ? null : index % 2 == 0 ? "PDF" : "ZPL", index % 7 == 0, created);
   }



   /// <summary>A throwaway agent store plus a direct SQLite connection to its journal, so tests can seed rows and ask SQLite for the reference ordering.</summary>
   private sealed class Fixture : IDisposable
   {
      private readonly string _directory = Path.Combine(Path.GetTempPath(), "qqq-sort-" + Guid.NewGuid().ToString("N"));
      private readonly SqliteConnection _database;
      public AgentStore Store { get; }



      /// <summary>Creates the store (and with it the journal schema) in a fresh temporary directory, then opens the journal for direct access.</summary>
      public Fixture()
      {
         Store = new AgentStore(_directory);
         _database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "journal.db") }.ToString());
         _database.Open();
      }



      /// <summary>Inserts the generated jobs in one transaction with parameterized statements and returns them as the expected snapshots.</summary>
      public JobSnapshot[] Seed(int count)
      {
         using SqliteTransaction transaction = _database.BeginTransaction();
         using SqliteCommand command = _database.CreateCommand();
         command.Transaction = transaction;
         command.CommandText = "INSERT INTO local_jobs(connection_id,job_id,title,printer_id,printer_name,format,state,created_at,updated_at,failure_acknowledged) VALUES('unenrolled',$id,$title,$printer,$name,$format,$state,$created,$updated,$reviewed)";
         foreach(string key in new[] { "$id", "$title", "$printer", "$name", "$format", "$state", "$created", "$updated", "$reviewed" })
         {
            command.Parameters.Add(key, SqliteType.Text);
         }
         List<JobSnapshot> jobs = [];
         for(int index = 0; index < count; index++)
         {
            JobSnapshot job = MakeJob(index, "job" + index.ToString("D5", CultureInfo.InvariantCulture));
            jobs.Add(job);
            command.Parameters["$id"].Value = job.JobId;
            command.Parameters["$title"].Value = (object?)job.Title ?? DBNull.Value;
            command.Parameters["$printer"].Value = (object?)job.PrinterId ?? DBNull.Value;
            command.Parameters["$name"].Value = (object?)job.PrinterName ?? DBNull.Value;
            command.Parameters["$format"].Value = (object?)job.Format ?? DBNull.Value;
            command.Parameters["$state"].Value = job.Status;
            command.Parameters["$created"].Value = (object?)job.CreatedAt ?? DBNull.Value;
            command.Parameters["$updated"].Value = job.UpdatedAt;
            command.Parameters["$reviewed"].Value = job.FailureAcknowledged ? 1 : 0;
            command.ExecuteNonQuery();
         }
         transaction.Commit();
         return jobs.ToArray();
      }



      /// <summary>Asks SQLite to compare two values with nulls first and case-insensitive text, the reference ordering the app's queries use.</summary>
      public int CompareSql(string? first, string? second)
      {
         using SqliteCommand command = _database.CreateCommand();
         command.CommandText = "SELECT CASE WHEN $a IS NULL THEN CASE WHEN $b IS NULL THEN 0 ELSE -1 END WHEN $b IS NULL THEN 1 WHEN $a COLLATE NOCASE < $b THEN -1 WHEN $a COLLATE NOCASE > $b THEN 1 ELSE 0 END";
         command.Parameters.AddWithValue("$a", (object?)first ?? DBNull.Value);
         command.Parameters.AddWithValue("$b", (object?)second ?? DBNull.Value);
         return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
      }



      /// <summary>Runs one raw statement against the journal, used to plant a corrupt row.</summary>
      public void Execute(string sql)
      {
         using SqliteCommand command = _database.CreateCommand();
         command.CommandText = sql;
         command.ExecuteNonQuery();
      }



      /// <summary>Closes both connections and clears SQLite's pool so the temporary journal file can be deleted on Windows.</summary>
      public void Dispose()
      {
         Store.Dispose();
         _database.Dispose();
         SqliteConnection.ClearAllPools();
         Directory.Delete(_directory, recursive: true);
      }
   }
}
