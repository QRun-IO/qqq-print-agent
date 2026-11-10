/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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
/// Harness for the path the installed service runs: a real agent runtime on a temporary store, signed in through a real session
/// client whose HTTP goes to a scripted server, and connected over a loopback WebSocket to a scripted realtime peer, with a queue
/// of synthetic jobs and a file-based fake printer. Tests drive what the server says (settings, state, commands,
/// acknowledgements), talk to the runtime over its management pipe, restart it on the same store, and read back what it sent.
/// The other parts of this class hold the scripted HTTP server, the realtime peer's connection and the printer.
/// </summary>
internal sealed partial class RealtimeRuntimeFixture : IAsyncDisposable
{
   internal LoopbackWebSocketPeer Peer { get; } = new();
   internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "qqq-native-runtime-" + Guid.NewGuid().ToString("N"));
   internal string PipeName { get; } = "qqq-realtime-fixture-" + Guid.NewGuid().ToString("N");
   internal RealtimeSettings Settings { get; set; } = new(Revision: 1, ReconnectMinDelayMillis: 100, ReconnectMaxDelayMillis: 1000, ReconnectJitterPercent: 0);
   internal ConcurrentQueue<PrintJob> Queue { get; } = new();
   internal ConcurrentDictionary<string, Func<Stream>> Documents { get; } = new(StringComparer.Ordinal);
   internal ConcurrentQueue<RealtimeEnvelope> Messages { get; } = new();
   internal ConcurrentQueue<string> Logs { get; } = new();
   internal SyntheticFilePrinter Printer { get; } = new();
   internal AgentRuntime Runtime { get; private set; }
   internal TaskCompletionSource? ReceivedRelease { get; set; }
   internal TaskCompletionSource? StateAppliedRelease { get; set; }
   internal Func<RealtimeEnvelope, Task>? BeforeAcknowledgement { get; set; }
   internal long LastHelloSettingsRevision;



   /// <summary>The running service loop of the current runtime; it completes only when the runtime stops, so a test can assert the service survived a failure.</summary>
   internal Task Running { get; private set; }



   /// <summary>Returns an error code to answer an agent message with a realtime ERROR frame instead of an acknowledgement (NOT_FOUND models a server that no longer knows the record), or null to acknowledge it.</summary>
   internal Func<RealtimeEnvelope, string?>? RefuseWith { get; set; }



   /// <summary>Returns true for an agent message the server must receive and then drop the connection without acknowledging, as a network failure at that exact moment would.</summary>
   internal Func<RealtimeEnvelope, bool>? LoseAcknowledgementOf { get; set; }



   /// <summary>
   /// When true the server behaves as a real one does when the agent reports an older desired-state revision as applied than
   /// the newest one it was sent: it sends that desired state again. When false it stays silent, which shows what a lost
   /// frame costs when nothing repairs it.
   /// </summary>
   internal bool ResendUnappliedState { get; set; }



   /// <summary>Runs on the runtime's command thread just before it saves a command's result; a test blocks here to hold that save open.</summary>
   internal Action? ControlResultSaving { get; set; }



   /// <summary>Captures the actual HELLO payload so compatibility tests observe the wire rather than duplicating capability selection.</summary>
   internal string[] LastHelloCapabilities { get; private set; } = [];



   /// <summary>Controls advertised discovery across a server upgrade or downgrade while retaining the same authenticated client session.</summary>
   internal string PrintingVersion { get; set; }



   /// <summary>Printer identifiers the fake server's operator catalog lists for this device; both shared queues by default.</summary>
   internal string[] ApprovedPrinters { get; set; } = ["printer", "other"];



   /// <summary>When set, the operator catalog answers with this status instead of a page, to model an older server (404) or a user without catalog access (403).</summary>
   internal HttpStatusCode? CatalogStatus { get; set; }

   private readonly Lock _claimLock = new();
   private PrintJob? _claimed;
   private readonly CancellationTokenSource _stopping = new(TimeSpan.FromSeconds(30));
   private readonly List<Connection> _connections = [];
   private readonly TimeProvider? _timeProvider;
   private CancellationTokenSource _running;



   /// <summary>
   /// Seeds an enrolled, signed-in agent store with two shared queues, lets the test add its own starting state, then starts the
   /// runtime with a session client wired to this fixture's fake HTTP server and loopback WebSocket. The concurrent-download
   /// limit and (optionally) cached realtime settings are set up front so tests start from a known negotiated state, and an
   /// HTTP fault can be in place before the runtime makes its first request.
   /// </summary>
   internal RealtimeRuntimeFixture(int downloads = 1, RealtimeSettings? settings = null, TimeProvider? timeProvider = null, bool cacheSettings = false, string printingVersion = "2.0.0",
      Action<AgentStore>? seed = null, Func<HttpRequestMessage, Exception?>? httpFault = null)
   {
      PrintingVersion = printingVersion;
      HttpFault = httpFault;
      _timeProvider = timeProvider;
      Settings = (settings ?? Settings) with { MaxConcurrentDownloads = downloads };
      using(var store = new AgentStore(DirectoryPath))
      {
         store.SaveConfiguration(new AgentConfiguration(Peer.Origin.AbsoluteUri, "device", "synthetic-proof"));
         store.SaveAuthentication(new AgentAuthentication(new QqqSessionBinding(Peer.Origin, "user", "tenant", "device"), new QqqSessionCredential(Peer.Origin, "synthetic-session")));
         store.SetExposure("printer", true, Printer.Discover());
         store.SetExposure("other", true, Printer.Discover());
         if(cacheSettings)
         { store.SaveRealtimeSettings(Settings); }
         seed?.Invoke(store);
      }
      Start();
   }



   /// <summary>Creates a runtime on the fixture's store and private pipe and starts its service loop.</summary>
   [MemberNotNull(nameof(Runtime), nameof(Running), nameof(_running))]
   private void Start()
   {
      _running = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
      Runtime = new AgentRuntime(DirectoryPath, Logs.Enqueue, Printer, pipeName: PipeName,
         pipeFactory: () => new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly),
         sessionFactory: origin => new QqqSessionClient(origin, new Handler(this), allowLoopbackHttp: true), timeProvider: _timeProvider)
      { ControlResultSaving = () => ControlResultSaving?.Invoke() };
      Running = Runtime.RunAsync(_running.Token);
   }



   /// <summary>Stops the current runtime and waits for its loop to end, tolerating the cancellations and closed sockets that a shutdown causes.</summary>
   private async Task StopAsync()
   {
      await _running.CancelAsync();
      try
      { await Running; }
      catch(Exception exception) when(exception is OperationCanceledException or IOException or WebSocketException) { }
      Runtime.Dispose();
      _running.Dispose();
   }



   /// <summary>
   /// Stops the service and starts a new one on the same store, pipe name and fake server, as a service restart does. Nothing
   /// held in memory survives, so what the new runtime knows is what the old one saved.
   /// </summary>
   internal async Task RestartAsync()
   {
      await StopAsync();
      Start();
   }



   /// <summary>
   /// Accepts the agent's WebSocket, requires HELLO first, records its settings revision and capabilities, answers WELCOME with
   /// the desired state and settings, and waits until the agent has applied them. With a connected desired state that means the
   /// agent reports itself connected; with a disconnected one, or when the test says traffic stays restricted, it means the
   /// transport is up, because ordinary traffic is then deliberately not allowed. A server channel that dies early fails with
   /// a redacted diagnostic instead of hanging; the only time limit is the fixture's own.
   /// </summary>
   internal async Task<Connection> ConnectAsync(bool loseSpoolAck = false, DeviceDesiredState? desiredState = null, bool restricted = false)
   {
      DeviceDesiredState desired = desiredState ?? new DeviceDesiredState(1, false, "CONNECTED");
      (WebSocket socket, RealtimeEnvelope hello) = await AcceptAgentAsync();
      var connection = new Connection(socket, this, loseSpoolAck, _stopping.Token);
      _connections.Add(connection);
      Assert.Equal("HELLO", hello.Type);
      LastHelloSettingsRevision = hello.Payload.GetProperty("lastSettingsRevision").GetInt64();
      LastHelloCapabilities = hello.Payload.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()!).ToArray();
      await connection.SendAsync("WELCOME", new { connectionId = Guid.NewGuid().ToString("N"), serverTime = Now(), desiredState = desired, settings = Settings });
      connection.Start();
      bool traffic = !restricted && desired.ConnectionState is "CONNECTED";
      await WaitAsync(() =>
      {
         if(connection.Completion.IsCompleted)
         { throw new InvalidOperationException(Diagnostic("initial realtime state synchronization: server channel stopped")); }
         return traffic ? Runtime.GetSnapshot().Connected : Runtime.GetTransportSnapshot().State is "CONNECTED";
      }, "initial realtime state synchronization");
      return connection;
   }



   /// <summary>
   /// Accepts the agent's next live socket and reads its first message. An attempt the agent abandoned while the upgrade was
   /// in flight (the service was restarted, or told to reconnect) is still in the listener's queue with nobody behind it; such
   /// a socket fails before any message arrives, and the live attempt queued after it is taken instead.
   /// </summary>
   private async Task<(WebSocket Socket, RealtimeEnvelope Hello)> AcceptAgentAsync()
   {
      while(true)
      {
         WebSocket? socket = null;
         try
         {
            socket = await Peer.AcceptAsync(_stopping.Token);
            return (socket, await LoopbackWebSocketPeer.ReceiveAsync(socket, _stopping.Token));
         }
         catch(OperationCanceledException exception) when(_stopping.IsCancellationRequested)
         { throw new InvalidOperationException(Diagnostic("WebSocket handshake"), exception); }
         catch(Exception exception) when(exception is IOException or WebSocketException)
         { socket?.Dispose(); }
      }
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



   /// <summary>Records a job that the Windows spooler already accepted (spooler id 123, receipt acknowledged) in the given store: the state a cancel command acts on.</summary>
   internal static void SeedSpooledJob(AgentStore store, string id = "job")
   {
      store.TrackJob(new PrintJob(id, "printer", "Synthetic " + id, "PDF", Convert.ToHexString(SHA256.HashData([1, 2, 3])), 3, new PrintOptions(), Now(), "CLAIMED"), "Synthetic");
      PrintEvent receipt = new("spooled-" + id, "SPOOLED", SpoolerJobId: "123");
      store.Record(id, receipt);
      store.Acknowledge(receipt.EventId);
   }



   /// <summary>Builds a server command, by default an unexpired cancel of the seeded job; expired shifts both timestamps into the past so staleness handling can be tested.</summary>
   internal static DeviceControlCommand Command(string action = "CANCEL_SPOOL", string id = "command", bool expired = false) => new(id, "request-" + id, action,
      DateTimeOffset.UtcNow.AddMinutes(expired ? -20 : -1).ToString("O", CultureInfo.InvariantCulture),
      DateTimeOffset.UtcNow.AddMinutes(expired ? -10 : 10).ToString("O", CultureInfo.InvariantCulture), string.Equals(action, "CANCEL_SPOOL", StringComparison.Ordinal) ? "job" : null);



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



   /// <summary>Waits for a signal a test double raises (a held claim arrived, a print started) under the fixture's limit, failing with the same diagnostic as a timed-out condition.</summary>
   internal async Task WaitAsync(Task signal, [CallerArgumentExpression(nameof(signal))] string stage = "")
   {
      try
      { await signal.WaitAsync(_stopping.Token); }
      catch(OperationCanceledException exception) when(_stopping.IsCancellationRequested)
      { throw new TimeoutException(Diagnostic(stage), exception); }
   }



   /// <summary>
   /// Lets exactly one heartbeat interval pass on a manual clock. It waits until the heartbeat loop is waiting on its timer,
   /// advances the clock by the interval, and waits until the loop is waiting again, by which time that round's frames have
   /// been handed to the socket. The heartbeat loop is the only thing in the runtime that waits on the clock.
   /// </summary>
   internal async Task HeartbeatAsync(ManualTimeProvider clock)
   {
      await WaitAsync(() => clock.PendingTimers > 0, "heartbeat loop waiting");
      clock.Advance(TimeSpan.FromSeconds(Settings.HeartbeatIntervalSeconds));
      await WaitAsync(() => clock.PendingTimers > 0, "heartbeat loop waiting again");
   }



   /// <summary>How many messages of one type the agent has sent so far.</summary>
   internal int Count(string type) => Messages.Count(message => string.Equals(message.Type, type, StringComparison.Ordinal));



   /// <summary>Sends one command over the runtime's management pipe, as the companion and command line do, and returns the reply.</summary>
   internal async Task<string> PipeAsync(PipeRequest request)
   {
      await using NamedPipeClientStream pipe = new(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
      await pipe.ConnectAsync(_stopping.Token);
      await AgentPipeFraming.WriteAsync(pipe, JsonSerializer.Serialize(request, PrintApiClient.JsonOptions), AgentPipeFraming.MaximumCommandCharacters, _stopping.Token);
      return await AgentPipeFraming.ReadAsync(pipe, AgentPipeFraming.MaximumResponseCharacters, _stopping.Token);
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



   /// <summary>Stops the runtime and every server connection, releasing anything a test left held, then removes the temporary store.</summary>
   public async ValueTask DisposeAsync()
   {
      await _stopping.CancelAsync();
      ClaimHold?.TrySetResult();
      Printer.Release.TrySetResult();
      foreach(Connection connection in _connections)
      { await connection.DisposeAsync(); }
      await StopAsync();
      Peer.Dispose();
      _stopping.Dispose();
      Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
      Directory.Delete(DirectoryPath, recursive: true);
   }
}
