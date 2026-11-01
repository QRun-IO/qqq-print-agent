/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Testing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Harness for the realtime (WebSocket) print path: a real agent runtime on a temporary store, connected over a loopback
/// WebSocket to a scripted server that speaks the realtime envelope protocol, with a queue of synthetic jobs and a file-based
/// fake printer. Tests drive what the server says (settings, state, acknowledgements) and read back what the agent sent.
/// </summary>
internal sealed class RealtimeRuntimeFixture : IAsyncDisposable
{
   internal LoopbackWebSocketPeer Peer { get; } = new();
   internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "qqq-native-runtime-" + Guid.NewGuid().ToString("N"));
   internal RealtimeSettings Settings { get; set; } = new(Revision: 1, ReconnectMinDelayMillis: 100, ReconnectMaxDelayMillis: 1000, ReconnectJitterPercent: 0);
   internal ConcurrentQueue<PrintJob> Queue { get; } = new();
   internal ConcurrentDictionary<string, Func<Stream>> Documents { get; } = new(StringComparer.Ordinal);
   internal ConcurrentQueue<RealtimeEnvelope> Messages { get; } = new();
   internal ConcurrentQueue<string> Logs { get; } = new();
   internal SyntheticFilePrinter Printer { get; } = new();
   internal AgentRuntime Runtime { get; }
   internal int Claims;
   internal volatile bool LastClaimWasEmpty;
   internal int ForbiddenPolls;
   internal TaskCompletionSource? ReceivedRelease { get; set; }
   internal TaskCompletionSource? StateAppliedRelease { get; set; }
   internal Func<RealtimeEnvelope, Task>? BeforeAcknowledgement { get; set; }
   internal long LastHelloSettingsRevision;



   /// <summary>Captures the actual HELLO payload so compatibility tests observe the wire rather than duplicating capability selection.</summary>
   internal string[] LastHelloCapabilities { get; private set; } = [];



   /// <summary>Counts discovery requests to distinguish reconnect reuse from an added HTTP polling schedule.</summary>
   internal int DiscoveryRequests;



   /// <summary>Controls advertised discovery across a server upgrade or downgrade while retaining the same authenticated client session.</summary>
   internal string PrintingVersion { get; set; }
   private readonly Lock _claimLock = new();
   private PrintJob? _claimed;
   private readonly CancellationTokenSource _stopping = new(TimeSpan.FromSeconds(30));
   private readonly Task _run;
   private readonly List<Connection> _connections = [];



   /// <summary>
   /// Seeds an enrolled, signed-in agent store with two shared queues, then starts the runtime with a session client wired to
   /// this fixture's fake HTTP server and loopback WebSocket. The concurrent-download limit and (optionally) cached realtime
   /// settings are set up front so tests start from a known negotiated state.
   /// </summary>
   internal RealtimeRuntimeFixture(int downloads = 1, RealtimeSettings? settings = null, TimeProvider? timeProvider = null, bool cacheSettings = false, string printingVersion = "2.0.0")
   {
      PrintingVersion = printingVersion;
      Settings = (settings ?? Settings) with { MaxConcurrentDownloads = downloads };
      using(var store = new AgentStore(DirectoryPath))
      {
         store.SaveConfiguration(new AgentConfiguration(Peer.Origin.AbsoluteUri, "device", "synthetic-proof"));
         store.SaveAuthentication(new AgentAuthentication(new QqqSessionBinding(Peer.Origin, "user", "tenant", "device"), new QqqSessionCredential(Peer.Origin, "synthetic-session")));
         store.SetExposure("printer", true, Printer.Discover());
         store.SetExposure("other", true, Printer.Discover());
         if(cacheSettings)
         { store.SaveRealtimeSettings(Settings); }
      }
      string pipe = "qqq-realtime-fixture-" + Guid.NewGuid().ToString("N");
      Runtime = new AgentRuntime(DirectoryPath, Logs.Enqueue, Printer, pipeName: pipe,
         pipeFactory: () => new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly),
         sessionFactory: origin => new QqqSessionClient(origin, new Handler(this), allowLoopbackHttp: true), timeProvider: timeProvider);
      _run = Runtime.RunAsync(_stopping.Token);
   }



   /// <summary>
   /// Accepts the agent's WebSocket, requires HELLO first, records its settings revision and capabilities, answers WELCOME with
   /// the desired state and settings, and waits until the agent reports itself connected. A missing handshake or a server channel
   /// that dies early fails with a redacted diagnostic instead of hanging.
   /// </summary>
   internal async Task<Connection> ConnectAsync(bool loseSpoolAck = false, DeviceDesiredState? desiredState = null)
   {
      WebSocket socket;
      try
      { socket = await Peer.AcceptAsync(_stopping.Token).WaitAsync(TimeSpan.FromSeconds(5)); }
      catch(TimeoutException exception) { throw new InvalidOperationException(Diagnostic("WebSocket handshake"), exception); }
      var connection = new Connection(socket, this, loseSpoolAck, _stopping.Token);
      _connections.Add(connection);
      RealtimeEnvelope hello = await LoopbackWebSocketPeer.ReceiveAsync(socket, _stopping.Token);
      Assert.Equal("HELLO", hello.Type);
      LastHelloSettingsRevision = hello.Payload.GetProperty("lastSettingsRevision").GetInt64();
      LastHelloCapabilities = hello.Payload.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()!).ToArray();
      await connection.SendAsync("WELCOME", new { connectionId = Guid.NewGuid().ToString("N"), serverTime = Now(), desiredState = desiredState ?? new DeviceDesiredState(1, false, "CONNECTED"), settings = Settings });
      connection.Start();
      await WaitAsync(() =>
      {
         if(connection.Completion.IsCompleted)
         { throw new InvalidOperationException(Diagnostic("initial realtime state synchronization: server channel stopped")); }
         return Runtime.GetSnapshot().Connected;
      }, "initial realtime state synchronization");
      return connection;
   }



   /// <summary>Current UTC time in round-trip format, as the server would stamp it.</summary>
   internal static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);



   /// <summary>
   /// Queues a synthetic PDF job and registers how its document is served. Without a custom stream the document is a few real
   /// bytes with a matching hash; with one, the declared length defaults to 25 MiB (or the given length) so large and failing
   /// downloads can be simulated.
   /// </summary>
   internal void AddJob(string id, Func<Stream>? content = null, string printerId = "printer", long? length = null)
   {
      byte[] bytes = Encoding.ASCII.GetBytes("%PDF-synthetic-" + id);
      Documents[id] = content ?? (() => new MemoryStream(bytes));
      Queue.Enqueue(new PrintJob(id, printerId, "Synthetic " + id, "PDF", Convert.ToHexString(SHA256.HashData(bytes)), length ?? (content is null ? bytes.Length : 25 * 1024 * 1024),
         new PrintOptions(), Now(), "QUEUED"));
   }



   /// <summary>Polls a condition every 20 ms until it holds, and on the fixture's 30-second limit fails with a diagnostic naming the awaited expression.</summary>
   internal async Task WaitAsync(Func<bool> predicate, [CallerArgumentExpression(nameof(predicate))] string stage = "")
   {
      try
      {
         while(!predicate())
         { await Task.Delay(20, _stopping.Token); }
      }
      catch(OperationCanceledException exception) when(_stopping.IsCancellationRequested)
      { throw new TimeoutException(Diagnostic(stage), exception); }
   }



   /// <summary>Builds a timeout message from message types and redacted, truncated log lines only, never payloads, cookies, proofs or documents.</summary>
   private string Diagnostic(string stage)
   {
      // Test evidence includes no envelope payload, cookie, proof, document or raw snapshot.
      string types = string.Join(",", Messages.TakeLast(12).Select(message => message.Type));
      string logs = string.Join("; ", Logs.TakeLast(8).Select(message => AgentActivityLog.Redact(message[..Math.Min(message.Length, 300)])));
      return $"Synthetic runtime wait failed at {stage[..Math.Min(stage.Length, 200)]}; received types=[{types}]; recent logs=[{logs}]";
   }



   /// <summary>Reads a job's recorded state from the agent's durable store, independent of what the agent reported over the wire.</summary>
   internal string? State(string id)
   {
      using var store = new AgentStore(DirectoryPath);
      return store.GetState(id);
   }



   /// <summary>Stops the runtime and every server connection, tolerating the cancellations that shutdown causes, then removes the temporary store.</summary>
   public async ValueTask DisposeAsync()
   {
      await _stopping.CancelAsync();
      foreach(Connection connection in _connections)
      { await connection.DisposeAsync(); }
      try
      { await _run; }
      catch(Exception exception) when(exception is OperationCanceledException or IOException or WebSocketException) { }
      Runtime.Dispose();
      Peer.Dispose();
      _stopping.Dispose();
      Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
      Directory.Delete(DirectoryPath, recursive: true);
   }



   /// <summary>
   /// A printer that accepts only the file-based submission the realtime path uses, counts and orders the jobs it receives, and
   /// remembers the submission timeout it was given. The in-memory print method throws, so a regression to the older path fails.
   /// </summary>
   internal sealed class SyntheticFilePrinter : IFilePrinterBackend
   {
      internal int Prints;
      internal TimeSpan LastSubmissionTimeout;
      internal ConcurrentQueue<string> Order { get; } = new();



      /// <summary>Reports two ready queues, "printer" and "other", each accepting PDF and ZPL.</summary>
      public IReadOnlyList<PrinterInfo> Discover() => [new("printer", "Synthetic", "Fixture", "None", "READY", ["PDF", "ZPL"]), new("other", "Other synthetic", "Fixture", "None", "READY", ["PDF", "ZPL"])];



      /// <summary>Always throws: the native realtime path must submit a file, not a byte array.</summary>
      public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken) => throw new InvalidOperationException("The native realtime path must use a file.");



      /// <summary>Checks the staged file's size matches the declared content length, records the job in order, and returns spooler id 123.</summary>
      public Task<string> PrintFileAsync(PrintJob job, string path, CancellationToken cancellationToken)
      {
         Assert.Equal(job.ContentLength, new FileInfo(path).Length);
         Interlocked.Increment(ref Prints);
         Order.Enqueue(job.JobId);
         return Task.FromResult("123");
      }



      /// <summary>Captures the submission timeout the agent chose so tests can assert it, then submits normally.</summary>
      public Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken)
      {
         LastSubmissionTimeout = submissionTimeout;
         return PrintFileAsync(job, path, cancellationToken);
      }
   }



   /// <summary>
   /// The server side of one accepted WebSocket. A background pump records every agent message, acknowledges it (the server's
   /// durable-receipt promise), answers pings, releases claimed jobs on RECEIVED, and can drop the connection at the SPOOLED event
   /// to simulate losing an acknowledgement.
   /// </summary>
   internal sealed class Connection(WebSocket socket, RealtimeRuntimeFixture fixture, bool loseSpoolAck, CancellationToken token) : IAsyncDisposable
   {
      private readonly SemaphoreSlim _send = new(1, 1);
      private Task _pump = Task.CompletedTask;
      internal Task Completion => _pump;



      /// <summary>Drops the socket without a close handshake, as a network failure would.</summary>
      internal void Abort() => socket.Abort();



      /// <summary>Begins reading and acknowledging agent messages.</summary>
      internal void Start() => _pump = PumpAsync();
      internal TaskCompletionSource LostSpoolAck { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);



      /// <summary>Sends one server message, serialized so concurrent senders cannot interleave frames.</summary>
      internal async Task SendAsync(string type, object payload)
      {
         await _send.WaitAsync(token);
         try
         { await LoopbackWebSocketPeer.SendAsync(socket, type, payload, token); }
         finally { _send.Release(); }
      }



      /// <summary>
      /// Reads agent messages until cancelled. Test hooks can hold acknowledgement (before-ack, state-applied, received-release) to
      /// create precise timing windows. A RECEIVED event frees the server's claimed job; PING gets PONG; otherwise ACK echoes the
      /// message id, except that the SPOOLED event can be left unacknowledged and the connection dropped.
      /// </summary>
      private async Task PumpAsync()
      {
         while(!token.IsCancellationRequested)
         {
            RealtimeEnvelope message = await LoopbackWebSocketPeer.ReceiveAsync(socket, token);
            fixture.Messages.Enqueue(message);
            if(fixture.BeforeAcknowledgement is not null)
            { await fixture.BeforeAcknowledgement(message); }
            if(message.Type is "STATE_APPLIED" && fixture.StateAppliedRelease is not null)
            { await fixture.StateAppliedRelease.Task.WaitAsync(token); }
            if(message.Type is "JOB_EVENT" && message.Payload.GetProperty("event").GetProperty("status").GetString() is "RECEIVED")
            {
               if(fixture.ReceivedRelease is not null)
               { await fixture.ReceivedRelease.Task.WaitAsync(token); }
               lock(fixture._claimLock)
               {
                  if(string.Equals(fixture._claimed?.JobId, message.Payload.GetProperty("jobId").GetString(), StringComparison.Ordinal))
                  { fixture._claimed = null; }
               }
            }
            if(message.Type is "PING")
            { await SendAsync("PONG", new { serverTime = Now() }); continue; }
            if(loseSpoolAck && message.Type is "JOB_EVENT" && message.Payload.GetProperty("event").GetProperty("status").GetString() is "SPOOLED")
            {
               LostSpoolAck.TrySetResult();
               socket.Abort();
               return;
            }
            await SendAsync("ACK", new { acknowledgedMessageId = message.MessageId });
         }
      }



      /// <summary>Aborts the socket, lets the pump finish and releases the socket and send gate.</summary>
      public async ValueTask DisposeAsync()
      {
         socket.Abort();
         try
         { await _pump; }
         catch(Exception exception) when(exception is OperationCanceledException or WebSocketException or IOException) { }
         socket.Dispose();
         _send.Dispose();
      }
   }



   /// <summary>
   /// Fake HTTP server for the session client: version discovery (advertising native sign-in and the configured printing
   /// version), document content, claims, the queued list, enrollment and session identity. Any other route is a forbidden
   /// polling or control call and is counted and thrown, which proves the realtime path adds no HTTP polling.
   /// </summary>
   private sealed class Handler(RealtimeRuntimeFixture fixture) : HttpMessageHandler
   {
      /// <summary>Routes by path; claims hand out the queue head under a lock so a job is claimed once until its RECEIVED event arrives.</summary>
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         string path = request.RequestUri!.AbsolutePath;
         if(string.Equals(path, "/qqq/v1/version", StringComparison.Ordinal))
         {
            Interlocked.Increment(ref fixture.DiscoveryRequests);
            return Response(JsonSerializer.Serialize(new
            {
               apiVersion = "1.9.0",
               features = new[] { "version", "metaData", "processes", "processCancel", "extensions", "widgets", "nativeSessionLogin" },
               clientChannels = new[] { "api" },
               extensions = new[] { new { name = "printing", version = fixture.PrintingVersion, pathPrefix = "/qbit/printing" } },
            }));
         }
         if(path.EndsWith("/content", StringComparison.Ordinal))
         {
            string id = path.Split('/')[^2];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(fixture.Documents[id]()) });
         }
         if(path.EndsWith("/claim", StringComparison.Ordinal))
         {
            Interlocked.Increment(ref fixture.Claims);
            lock(fixture._claimLock)
            {
               if(fixture._claimed is null)
               { fixture.Queue.TryDequeue(out fixture._claimed); }
               fixture.LastClaimWasEmpty = fixture._claimed is null;
               return Response(JsonSerializer.Serialize(new { job = fixture._claimed }, PrintApiClient.JsonOptions));
            }
         }
         if(path.EndsWith("/queued", StringComparison.Ordinal))
         {
            return Response(JsonSerializer.Serialize(new { jobs = fixture.Queue.ToArray() }, PrintApiClient.JsonOptions));
         }
         if(path.EndsWith("/devices/enroll", StringComparison.Ordinal))
         { return Response("""{"protocolVersion":1,"deviceId":"replacement-device","deviceToken":"replacement-proof","pollIntervalSeconds":5}"""); }
         if(path.EndsWith("/printing/session", StringComparison.Ordinal))
         {
            string deviceId = request.Headers.TryGetValues("X-QQQ-Device-Id", out IEnumerable<string>? ids) ? ids.Single() : "device";
            return Response(JsonSerializer.Serialize(new { principalId = "user", tenantId = "tenant", deviceId }));
         }
         return Response(path switch
         {
            "/qqq/v1/manageSession" => """{"uuid":"synthetic-session"}""",
            "/qqq/v1/metaData/authentication" => """{"name":"local","type":"TABLE_BASED"}""",
            "/qqq/v1/qbit/printing/session" => """{"principalId":"user","tenantId":"tenant","deviceId":"device"}""",
            _ => Unexpected(path),
         });
      }



      /// <summary>Counts and rejects a route the realtime path must never call.</summary>
      private string Unexpected(string path)
      {
         Interlocked.Increment(ref fixture.ForbiddenPolls);
         throw new InvalidOperationException("Unexpected HTTP polling/control route: " + path);
      }



      /// <summary>A 200 response with the given text body.</summary>
      private static Task<HttpResponseMessage> Response(string value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(value) });
   }
}
