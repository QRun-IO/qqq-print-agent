/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Security.Cryptography;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// The limits Core enforces itself are counted on an injected clock: the acknowledgement wait, the session client's
/// request limit, the control limits, the three download limits, the bandwidth pacing and the worker's phase timings.
/// The one limit that is not is <c>HttpClient.Timeout</c> on the API client's HTTP client, which the platform counts
/// on the system clock; it is not tested here. These tests move the injected clock by hand and check that each limit
/// fires at exactly its configured time and not a millisecond earlier. "Not earlier" is read off the clock itself:
/// one millisecond before the limit, the limit's timer must still be armed and due in exactly that millisecond, which
/// it would not be had it already fired. None of the tests waits for real time to pass.
/// </summary>
public sealed class InjectedClockTests
{
   private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);
   private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);



   /// <summary>
   /// A send that needs the server's ACK fails with a timeout when the negotiated acknowledgement time passes on the
   /// channel's clock, and its message identity is then free again: the caller's replay of the same durable record is
   /// accepted and can be acknowledged.
   /// </summary>
   [Fact]
   public async Task AcknowledgedSendTimesOutAtTheNegotiatedLimitAndCanBeReplayed()
   {
      var time = new ManualTimeProvider();
      using var socket = new ScriptedWebSocket();
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings(AckTimeoutMillis: 15_000), timeProvider: time);
      using var stop = new CancellationTokenSource();
      Task run = channel.RunAsync(_ => { }, stop.Token);
      Task send = channel.SendAsync("JOB_EVENT", new { jobId = "job-1" }, "event-1", acknowledged: true);
      Assert.Equal("event-1", (await socket.NextSentAsync()).GetProperty("messageId").GetString());
      await time.WhenDueInAsync(TimeSpan.FromSeconds(15));
      time.Advance(TimeSpan.FromSeconds(15) - Tick);
      Assert.Contains(Tick, time.DueIn);
      time.Advance(Tick);
      await Assert.ThrowsAsync<TimeoutException>(() => send);

      Task replay = channel.SendAsync("JOB_EVENT", new { jobId = "job-1" }, "event-1", acknowledged: true);
      Assert.Equal("event-1", (await socket.NextSentAsync()).GetProperty("messageId").GetString());
      socket.Deliver("ACK", "ack-1", new { acknowledgedMessageId = "event-1" });
      await replay.WaitAsync(Patience);
      await stop.CancelAsync();
      await run.WaitAsync(TimeSpan.FromSeconds(10));
   }



   /// <summary>A request the server never answers is abandoned after twenty seconds on the session client's clock, and the log reports it as a timeout with the elapsed time measured on that same clock.</summary>
   [Fact]
   public async Task UnansweredSessionRequestTimesOutAfterTwentySeconds()
   {
      var time = new ManualTimeProvider();
      var info = new List<string>();
      var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      using var server = new ScriptedServer();
      server.AddStalled(arrived);
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, time, diagnostics: new QqqSessionDiagnostics(info.Add));
      Task<QqqApiDiscovery> discovery = client.DiscoverAsync();
      await arrived.Task.WaitAsync(Patience);
      time.Advance(TimeSpan.FromSeconds(20) - Tick);
      Assert.Contains(Tick, time.DueIn);
      time.Advance(Tick);
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery);
      Assert.Contains("HTTP GET https://printing.example.invalid/qqq/v1/version timed out after 20000 ms with no response headers.", info, StringComparer.Ordinal);
   }



   /// <summary>
   /// A printing request is bounded by its caller, not by the twenty second limit: it is still waiting after ten minutes
   /// on the client's clock, ends when the caller cancels, and is logged as cancelled by the caller rather than timed out.
   /// </summary>
   [Fact]
   public async Task PrintingRequestIsBoundedOnlyByItsCaller()
   {
      var time = new ManualTimeProvider();
      var info = new List<string>();
      var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      using ScriptedServer server = ScriptedServer.SignedIn().AddStalled(arrived);
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, time, diagnostics: new QqqSessionDiagnostics(info.Add));
      await client.SignInWithPasswordAsync("synthetic-user", "synthetic-password");
      using var caller = new CancellationTokenSource();
      Task<HttpResponseMessage> request = client.SendPrintingAsync(HttpMethod.Get, "admin/devices", cancellationToken: caller.Token);
      await arrived.Task.WaitAsync(Patience);
      Assert.Empty(time.DueIn);
      time.Advance(TimeSpan.FromMinutes(10));
      Assert.False(request.IsCompleted);
      await caller.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
      Assert.Contains("HTTP GET https://printing.example.invalid/qqq/v1/qbit/printing/admin/devices was cancelled by the caller after 600000 ms.", info, StringComparer.Ordinal);
   }



   /// <summary>A document request that gets no response headers is abandoned at the connect limit, well before the total limit, and leaves nothing in the staging folder.</summary>
   [Fact]
   public async Task DownloadGivesUpAtTheConnectLimit()
   {
      var time = new ManualTimeProvider();
      var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      using var staging = new TempDirectory();
      using var server = new ScriptedServer();
      server.AddStalled(arrived);
      using var api = new PrintApiClient("https://synthetic.invalid", handler: server, timeProvider: time);
      Task<DownloadedDocument> download = api.DownloadFileAsync(Job(new byte[8]), staging.Path, Limits(), CancellationToken.None);
      await arrived.Task.WaitAsync(Patience);
      time.Advance(TimeSpan.FromSeconds(2) - Tick);
      Assert.Contains(Tick, time.DueIn);
      time.Advance(Tick);
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
      Assert.Empty(Directory.EnumerateFileSystemEntries(staging.Path));
   }



   /// <summary>A body that stops arriving is abandoned at the idle limit, measured from the start of the read that stalled, and the partial file is deleted.</summary>
   [Fact]
   public async Task DownloadGivesUpWhenTheBodyIsIdleForTheIdleLimit()
   {
      var time = new ManualTimeProvider();
      using var staging = new TempDirectory();
      using var body = new FeedStream();
      using var server = new ScriptedServer();
      server.AddResponse(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
      using var api = new PrintApiClient("https://synthetic.invalid", handler: server, timeProvider: time);
      Task<DownloadedDocument> download = api.DownloadFileAsync(Job(new byte[8]), staging.Path, Limits(), CancellationToken.None);
      await body.NextReadAsync();
      time.Advance(TimeSpan.FromSeconds(2));
      body.Feed([1, 2, 3, 4]);
      await body.NextReadAsync();
      time.Advance(TimeSpan.FromSeconds(3) - Tick);
      Assert.Contains(Tick, time.DueIn);
      time.Advance(Tick);
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
      Assert.Empty(Directory.EnumerateFileSystemEntries(staging.Path));
   }



   /// <summary>A body that keeps trickling, never idle for the idle limit, is still abandoned when the whole transfer reaches the total limit.</summary>
   [Fact]
   public async Task DownloadGivesUpAtTheTotalLimitEvenWhenNeverIdle()
   {
      var time = new ManualTimeProvider();
      using var staging = new TempDirectory();
      using var body = new FeedStream();
      using var server = new ScriptedServer();
      server.AddResponse(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
      using var api = new PrintApiClient("https://synthetic.invalid", handler: server, timeProvider: time);
      Task<DownloadedDocument> download = api.DownloadFileAsync(Job(new byte[8]), staging.Path, Limits(), CancellationToken.None);
      for(int chunk = 0; chunk < 3; chunk++)
      {
         await body.NextReadAsync();
         time.Advance(TimeSpan.FromSeconds(2));
         body.Feed([(byte)chunk]);
      }
      await body.NextReadAsync();
      time.Advance(TimeSpan.FromSeconds(1) - Tick);
      Assert.Contains(Tick, time.DueIn);
      time.Advance(Tick);
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
      Assert.Equal(TimeSpan.FromSeconds(7), time.Elapsed);
      Assert.Empty(Directory.EnumerateFileSystemEntries(staging.Path));
   }



   /// <summary>
   /// With a per-download rate limit and no shared throttle, each chunk is followed by exactly the pause that keeps the
   /// average at the limit: 2,500 bytes at 1,000 bytes per second take 2.5 seconds on the client's clock, and the next
   /// read does not start until its pause has passed.
   /// </summary>
   [Fact]
   public async Task RateLimitPausesExactlyLongEnoughToHoldTheAverage()
   {
      var time = new ManualTimeProvider();
      byte[] document = RandomNumberGenerator.GetBytes(2_500);
      using var staging = new TempDirectory();
      using var body = new FeedStream();
      using var server = new ScriptedServer();
      server.AddResponse(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
      using var api = new PrintApiClient("https://synthetic.invalid", handler: server, timeProvider: time);
      Task<DownloadedDocument> download = api.DownloadFileAsync(Job(document), staging.Path, Limits() with { MaxDownloadBytesPerSecond = 1_000 }, CancellationToken.None);
      await body.NextReadAsync();
      body.Feed(document[..1_000]);
      await time.WhenDueInAsync(TimeSpan.FromSeconds(1));
      Assert.Equal(1, body.Reads);
      time.Advance(TimeSpan.FromSeconds(1));
      await body.NextReadAsync();
      body.Feed(document[1_000..]);
      await time.WhenDueInAsync(TimeSpan.FromSeconds(1.5));
      Assert.Equal(2, body.Reads);
      time.Advance(TimeSpan.FromSeconds(1.5));
      await body.NextReadAsync();
      body.End();
      using DownloadedDocument downloaded = await download.WaitAsync(Patience);
      Assert.Equal(document, await File.ReadAllBytesAsync(downloaded.Path));
      Assert.Equal(TimeSpan.FromSeconds(2.5), time.Elapsed);
   }



   /// <summary>The in-memory download used without a staging folder gives up when the request limit passes on the API client's clock.</summary>
   [Fact]
   public async Task InMemoryDownloadGivesUpAtTheRequestLimit()
   {
      var time = new ManualTimeProvider();
      using var body = new FeedStream();
      using var server = new ScriptedServer();
      server.AddResponse(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
      using var api = new PrintApiClient("https://synthetic.invalid", handler: server, timeProvider: time);
      Task<byte[]> download = api.DownloadAsync(Job(new byte[8]), CancellationToken.None);
      await body.NextReadAsync();
      time.Advance(TimeSpan.FromSeconds(60) - Tick);
      Assert.Contains(Tick, time.DueIn);
      time.Advance(Tick);
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
   }



   /// <summary>
   /// The per-job timing line reports each phase as measured on the worker's clock: inventory, the claim with its receipt,
   /// the download, the printer recheck, the submission gate with its receipt, and the native submission.
   /// </summary>
   [Fact]
   public async Task WorkerTimingLineReportsEachPhaseFromItsClock()
   {
      var time = new ManualTimeProvider();
      byte[] document = [1, 2, 3];
      using var server = new FakePrintServer
      {
         Arriving = (kind, _) =>
         {
            time.Advance(TimeSpan.FromMilliseconds(kind switch { "claim" => 7, "content" => 11, "events" => 2, _ => 0 }));
            return Task.CompletedTask;
         },
      };
      server.Offer(FakePrintServer.Job(document), document);
      using var api = new PrintApiClient("http://localhost", "synthetic-device-token", server);
      var printer = new FakePrinterBackend
      {
         Discovering = () => time.Advance(TimeSpan.FromMilliseconds(5)),
         Printing = (_, _) =>
         {
            time.Advance(TimeSpan.FromMilliseconds(13));
            return Task.FromResult("spool-1");
         },
      };
      var lines = new List<string>();
      Assert.True(await new PrintWorker(api, printer, new RecordingJournal(), lines.Add, options: new PrintWorkerOptions { TimeProvider = time }).RunOnceAsync());
      Assert.Contains("job-1: timing inventory=5ms claim=9ms download=11ms revalidate=5ms gate=2ms submit=13ms", lines, StringComparer.Ordinal);
   }



   /// <summary>A synthetic PDF job whose declared length and hash match the given document.</summary>
   private static PrintJob Job(byte[] document) => FakePrintServer.Job(document, format: "PDF");



   /// <summary>Short download limits that still satisfy the policy rules: connect 2 s, idle 3 s, total 7 s.</summary>
   private static RealtimeSettings Limits() => new(DownloadConnectTimeoutMillis: 2_000, DownloadIdleTimeoutMillis: 3_000, DownloadTimeoutMillis: 7_000);
}
