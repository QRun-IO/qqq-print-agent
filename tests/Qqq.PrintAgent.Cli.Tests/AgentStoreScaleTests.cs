/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Testing;
using Qqq.PrintAgent.Windows;
using Xunit.Abstractions;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Volume checks for the local journal: history views, retention and the event outbox stay bounded as the store grows to tens of thousands of jobs.</summary>
public sealed class AgentStoreScaleTests(ITestOutputHelper output)
{
   /// <summary>
   /// With 10,000 and 30,000 seeded jobs, pages are limited to 100 and do not overlap, counts for status, attention, printer and
   /// search filters are exact, and wildcard or quote characters in a search are treated as plain text. Pruning keeps the default
   /// detail budget untouched, trims to a one-job budget when asked, and still protects an unreviewed failure's evidence. The
   /// generous 30-second ceiling only catches unbounded growth; it is not a performance benchmark.
   /// </summary>
   [Theory]
   [InlineData(10000)]
   [InlineData(30000)]
   public void LargeHistory_QueriesPagesCountsAndRetainsEvidence(int count)
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-scale-" + Guid.NewGuid().ToString("N"));
      try
      {
         using JournalStore store = TestState.OpenStore(directory);
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
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>
   /// Ten thousand unacknowledged events are sent at most 200 per pass, each as a realtime frame the server acknowledges,
   /// and a new job is claimed only once the outbox is empty (the claim happens on the 50th pass, not before), so a large
   /// backlog cannot starve receipts or grow a pass unboundedly.
   /// </summary>
   [Fact]
   public async Task TenThousandPendingEvents_DrainInBoundedBatchesBeforeAnotherClaim()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-outbox-scale-" + Guid.NewGuid().ToString("N"));
      using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(5));
      using LoopbackWebSocketPeer peer = new();
      using ClientWebSocket client = new();
      client.Options.AddSubProtocol("qqq-printing.v1");
      Task<WebSocket> accepting = peer.AcceptAsync(limit.Token);
      await client.ConnectAsync(new Uri("ws://127.0.0.1:" + peer.Origin.Port.ToString(CultureInfo.InvariantCulture) + "/qqq/v1/qbit/printing/device/realtime"), limit.Token);
      AcknowledgingServer server = new(await accepting);
      using PrintingRealtimeChannel channel = new(client, new RealtimeSettings());
      Task running = channel.RunAsync(_ => { }, limit.Token);
      Task acknowledging = server.RunAsync(limit.Token);
      try
      {
         using JournalStore store = TestState.OpenStore(directory);
         Seed(directory, 10000, pending: true);
         using SessionFixture.FakeSession session = new();
         using PrintApiClient api = PrintApiClient.ForSession(session).WithRealtime(channel, new RealtimeSettings());
         using SemaphoreSlim eventGate = new(1, 1);
         using SemaphoreSlim claimGate = new(1, 1);
         PrintWorker worker = new(api, new NoPrinter(), store, _ => { }, () => true, () => true, LaneOptions(directory, eventGate, claimGate));
         long start = Stopwatch.GetTimestamp();
         for(int batch = 0; batch < 50; batch++)
         {
            Assert.Equal(200, store.Pending().Count);
            await worker.RunOnceAsync(limit.Token);
            Assert.Equal((batch + 1) * 200, server.Events);
            Assert.Equal(batch == 49 ? ["device/jobs/claim"] : [], session.Paths);
         }
         Assert.Empty(store.Pending());
         output.WriteLine($"10,000 durable event acknowledgements: {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms; 200 events maximum per pass.");
      }
      finally
      {
         await limit.CancelAsync();
         await Task.WhenAll(running, acknowledging).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>The hooks a lane is given, for a worker that only drains its outbox and claims: the gates are real, and every hook a job would use fails the test, because no job is ever offered.</summary>
   private static PrintWorkerOptions LaneOptions(string directory, SemaphoreSlim eventGate, SemaphoreSlim claimGate) => new()
   {
      StagingDirectory = Path.Combine(directory, "staging"),
      Settings = new RealtimeSettings(),
      CurrentSettings = () => new RealtimeSettings(),
      CanSubmit = () => true,
      EventGate = eventGate,
      ClaimGate = claimGate,
      ThrottleDownloadAsync = (_, _) => Task.CompletedTask,
      IsInHand = _ => false,
      CanClaim = () => true,
      ReserveDocumentAsync = (_, _) => throw new InvalidOperationException("Scale tests must not print."),
      AcquireSubmissionAsync = (_, _) => throw new InvalidOperationException("Scale tests must not print."),
      ActiveJobChanged = _ => { },
   };



   /// <summary>
   /// Reading one page must not parse stored JSON for rows outside it, and reopening the store must not re-run the one-time
   /// migration of legacy events. A deliberately corrupt row and a legacy-only event would break these calls if either work
   /// were repeated on every query or open.
   /// </summary>
   [Fact]
   public void Query_DoesNotDeserializeUnselectedHistoryOrRepeatLegacyBackfill()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-query-bound-" + Guid.NewGuid().ToString("N"));
      try
      {
         using(JournalStore store = TestState.OpenStore(directory))
         {
            Seed(directory, 10000, pending: false);
         }
         using(SqliteConnection database = Open(directory))
         {
            using SqliteCommand command = database.CreateCommand();
            command.CommandText = "UPDATE local_jobs SET last_event_json='not-json' WHERE job_id='job000000'; INSERT INTO events(job_id,event_id,state,json,connection_id) VALUES('legacy-only','legacy-only','SPOOLED','not-json','unenrolled');";
            command.ExecuteNonQuery();
         }
         using JournalStore reopened = TestState.OpenStore(directory);
         Assert.Null(reopened.GetState("legacy-only")); // Existing summary migration is not replayed on every open.
         Assert.Single(reopened.QueryJobs(new JobQuery(Search: "Packing 000001")).Jobs);
         Assert.Equal(10000, reopened.QueryJobs(new JobQuery(PageSize: 1)).TotalCount);
      }
      finally
      {
         JournalFiles.Delete(directory);
      }
   }



   /// <summary>Opens a direct connection to the store's journal file for seeding and tampering.</summary>
   private static SqliteConnection Open(string directory) => TestState.OpenJournal(directory);



   /// <summary>
   /// Inserts count jobs and one event each in a single transaction: every tenth job FAILED, printers alternating, titles
   /// numbered, and events marked acknowledged unless pending is requested.
   /// </summary>
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



   /// <summary>The server's end of the realtime connection: it counts each job event it receives and acknowledges it, so the worker's delivery completes and the journal marks the event delivered.</summary>
   private sealed class AcknowledgingServer(WebSocket socket)
   {
      private int _events;



      /// <summary>How many job events arrived.</summary>
      internal int Events => Volatile.Read(ref _events);



      /// <summary>Reads frames until the test stops it. An event is counted before its acknowledgement is sent, so the count is settled by the time the worker's delivery returns.</summary>
      internal async Task RunAsync(CancellationToken token)
      {
         byte[] buffer = new byte[65536];
         while(true)
         {
            WebSocketReceiveResult frame = await socket.ReceiveAsync(buffer, token);
            Assert.True(frame.EndOfMessage);
            using var message = JsonDocument.Parse(buffer.AsMemory(0, frame.Count));
            Assert.Equal("JOB_EVENT", message.RootElement.GetProperty("type").GetString());
            Interlocked.Increment(ref _events);
            await LoopbackWebSocketPeer.SendAsync(socket, "ACK", new { acknowledgedMessageId = message.RootElement.GetProperty("messageId").GetString() }, token);
         }
      }
   }



   /// <summary>Printer backend with no queues that throws if asked to print.</summary>
   private sealed class NoPrinter : IPrinterBackend
   {
      /// <summary>Reports an empty printer inventory; the scale tests drive the store and the receipt outbox and need no queue.</summary>
      public IReadOnlyList<PrinterInfo> Discover() => [];



      /// <summary>Reports the same empty inventory.</summary>
      public IReadOnlyList<PrinterInfo> ListWithoutMedia() => [];



      /// <summary>Has no queue to find.</summary>
      public PrinterInfo? FindWithMedia(string printerId) => null;



      /// <summary>Always throws: scale tests must not print.</summary>
      public Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken) => throw new InvalidOperationException("Scale tests must not print.");
   }
}
