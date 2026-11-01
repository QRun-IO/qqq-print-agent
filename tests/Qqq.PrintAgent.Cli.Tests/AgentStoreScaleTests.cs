/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;
using Xunit.Abstractions;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentStoreScaleTests(ITestOutputHelper output)
{
   [Theory]
   [InlineData(10000)]
   [InlineData(30000)]
   public void LargeHistory_QueriesPagesCountsAndRetainsEvidence(int count)
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-scale-" + Guid.NewGuid().ToString("N"));
      try
      {
         using AgentStore store = new(directory);
         long start = Stopwatch.GetTimestamp();
         Seed(directory, count, pending: false);
         TimeSpan seedElapsed = Stopwatch.GetElapsedTime(start);
         start = Stopwatch.GetTimestamp();
         JobPage first = store.QueryJobs(new JobQuery(PageSize: 100));
         JobPage second = store.QueryJobs(new JobQuery(Offset: 100, PageSize: 100));
         Assert.Equal(count, first.TotalCount);
         Assert.Equal(100, first.Jobs.Count);
         Assert.Empty(first.Jobs.Select(job => job.JobId).Intersect(second.Jobs.Select(job => job.JobId), StringComparer.Ordinal));
         Assert.Equal(count / 10, store.QueryJobs(new JobQuery(Status: "FAILED")).TotalCount);
         Assert.Equal(count / 10, store.NeedsAttentionCount());
         Assert.Equal(count / 10, store.QueryJobs(new JobQuery(NeedsAttentionOnly: true)).TotalCount);
         Assert.Equal(count / 2, store.QueryJobs(new JobQuery(PrinterId: "laser")).TotalCount);
         Assert.Equal(1000, store.QueryJobs(new JobQuery(Search: "Packing 000")).TotalCount);
         Assert.Empty(store.QueryJobs(new JobQuery(Search: "%")).Jobs);
         Assert.Empty(store.QueryJobs(new JobQuery(Search: "_'")).Jobs);
         Assert.Equal(0, store.PruneHistory()); // Three days at 10k/day fit the default detail budget.
         Assert.Equal(250, store.PruneHistory(maxDetailedJobs: 1));
         Assert.NotNull(store.GetState("job000001"));
         Assert.Single(store.GetJobDetails("job000000")!.Events); // Unreviewed failure protected.
         Assert.Empty(store.Pending());
         Assert.Throws<ArgumentOutOfRangeException>(() => store.QueryJobs(new JobQuery(PageSize: 101)));
         output.WriteLine($"{count} jobs: seed {seedElapsed.TotalMilliseconds:F0} ms; query/count/retention {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms.");
         Assert.True(Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(30), "Bounded history queries exceeded the generous regression budget.");
      }
      finally
      {
         SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
      }
   }

   [Fact]
   public async Task TenThousandPendingEvents_DrainInBoundedBatchesBeforeAnotherClaim()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-outbox-scale-" + Guid.NewGuid().ToString("N"));
      try
      {
         using AgentStore store = new(directory);
         Seed(directory, 10000, pending: true);
         using CountingHandler handler = new();
         using PrintApiClient api = new("http://localhost", "synthetic", handler);
         PrintWorker worker = new(api, new NoPrinter(), store);
         long start = Stopwatch.GetTimestamp();
         for(int batch = 0; batch < 50; batch++)
         {
            Assert.Equal(200, store.Pending().Count);
            await worker.RunOnceAsync();
            Assert.Equal((batch + 1) * 200, handler.Events);
            Assert.Equal(batch == 49 ? 1 : 0, handler.Claims);
         }
         Assert.Empty(store.Pending());
         output.WriteLine($"10,000 durable event acknowledgements: {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms; 200 events maximum per poll.");
      }
      finally
      {
         SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
      }
   }

   [Fact]
   public void Query_DoesNotDeserializeUnselectedHistoryOrRepeatLegacyBackfill()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-query-bound-" + Guid.NewGuid().ToString("N"));
      try
      {
         using(AgentStore store = new(directory))
         {
            Seed(directory, 10000, pending: false);
         }
         using(SqliteConnection database = Open(directory))
         {
            using SqliteCommand command = database.CreateCommand();
            command.CommandText = "UPDATE local_jobs SET last_event_json='not-json' WHERE job_id='job000000'; INSERT INTO events(job_id,event_id,state,json,connection_id) VALUES('legacy-only','legacy-only','SPOOLED','not-json','unenrolled');";
            command.ExecuteNonQuery();
         }
         using AgentStore reopened = new(directory);
         Assert.Null(reopened.GetState("legacy-only")); // Existing summary migration is not replayed on every open.
         Assert.Single(reopened.QueryJobs(new JobQuery(Search: "Packing 000001")).Jobs);
         Assert.Equal(10000, reopened.QueryJobs(new JobQuery(PageSize: 1)).TotalCount);
      }
      finally
      {
         SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
      }
   }

   private static SqliteConnection Open(string directory)
   {
      SqliteConnection database = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "journal.db") }.ToString());
      database.Open();
      return database;
   }

   private static void Seed(string directory, int count, bool pending)
   {
      using SqliteConnection database = Open(directory);
      using SqliteTransaction transaction = database.BeginTransaction();
      using SqliteCommand command = database.CreateCommand();
      command.Transaction = transaction;
      command.CommandText = "INSERT INTO local_jobs(connection_id,job_id,printer_id,printer_name,title,format,state,last_event_json,updated_at) VALUES('unenrolled',$id,$printer,'Warehouse laser',$title,'PDF',$state,$json,$date); INSERT INTO events(job_id,event_id,state,json,acknowledged,recorded_at,connection_id) VALUES($id,$id,$state,$json,$ack,$date,'unenrolled');";
      foreach(string name in new[] { "$id", "$printer", "$title", "$state", "$json", "$date" })
      {
         command.Parameters.Add(name, SqliteType.Text);
      }
      command.Parameters.AddWithValue("$ack", pending ? 0 : 1);
      DateTimeOffset now = DateTimeOffset.UtcNow;
      for(int index = 0; index < count; index++)
      {
         string number = index.ToString("D6", CultureInfo.InvariantCulture);
         string state = index % 10 == 0 ? "FAILED" : "SPOOLED";
         command.Parameters["$id"].Value = "job" + number;
         command.Parameters["$printer"].Value = index % 2 == 0 ? "laser" : "zebra";
         command.Parameters["$title"].Value = "Packing " + number;
         command.Parameters["$state"].Value = state;
         command.Parameters["$json"].Value = JsonSerializer.Serialize(new PrintEvent("job" + number, state), PrintApiClient.JsonOptions);
         command.Parameters["$date"].Value = now.AddSeconds(index - count).ToString("O", CultureInfo.InvariantCulture);
         command.ExecuteNonQuery();
      }
      transaction.Commit();
   }

   private sealed class CountingHandler : HttpMessageHandler
   {
      public int Events { get; private set; }
      public int Claims { get; private set; }

      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         string path = request.RequestUri!.AbsolutePath;
         if(path.EndsWith("/events", StringComparison.Ordinal))
         {
            Events++;
         }
         if(path.EndsWith("/claim", StringComparison.Ordinal))
         {
            Claims++;
         }
         return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"job\":null}", Encoding.UTF8, "application/json") });
      }
   }

   private sealed class NoPrinter : IPrinterBackend
   {
      public IReadOnlyList<PrinterInfo> Discover() => [];
      public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken) => throw new InvalidOperationException("Scale tests must not print.");
   }
}
