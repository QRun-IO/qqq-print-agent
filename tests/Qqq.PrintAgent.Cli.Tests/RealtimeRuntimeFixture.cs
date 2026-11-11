/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Harness for the path the installed service runs: a real agent runtime on a temporary store, signed in through a real session
/// client to one scripted server (its HTTP handler, its loopback realtime peer and its queue of synthetic jobs), with a
/// file-based fake printer. Tests drive what the server says (settings, state, commands, acknowledgements), talk to the
/// runtime over its management pipe, restart it on the same store, and read back what it sent. The server's members that
/// tests use are repeated here, in another part of this class, so a test of one server reads as it always has; the printer
/// is in a third part.
/// </summary>
internal sealed partial class RealtimeRuntimeFixture : IAsyncDisposable
{
   /// <summary>The one server this runtime is paired with; its origin is the server address saved in the store.</summary>
   internal ScriptedPrintServer Server { get; }



   /// <summary>The temporary folder that holds the agent's store, removed when the fixture is disposed.</summary>
   internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "qqq-native-runtime-" + Guid.NewGuid().ToString("N"));



   /// <summary>The private name of the runtime's management pipe, so tests running side by side never share one.</summary>
   internal string PipeName { get; } = "qqq-realtime-fixture-" + Guid.NewGuid().ToString("N");



   /// <summary>The runtime's log lines, in order.</summary>
   internal ConcurrentQueue<string> Logs { get; } = new();



   /// <summary>The fake printer behind both shared queues.</summary>
   internal SyntheticFilePrinter Printer { get; } = new();



   /// <summary>The runtime under test; replaced by <see cref="RestartAsync"/>.</summary>
   internal SingleServerHarness Runtime { get; private set; }



   /// <summary>The running service loop of the current runtime; it completes only when the runtime stops, so a test can assert the service survived a failure.</summary>
   internal Task Running { get; private set; }



   /// <summary>Runs on the runtime's command thread just before it saves a command's result; a test blocks here to hold that save open.</summary>
   internal Action? ControlResultSaving { get; set; }

   private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);
   private readonly CancellationTokenSource _stopping = new();
   private readonly TimeProvider? _timeProvider;
   private CancellationTokenSource _running;



   /// <summary>The printers the runtime is given in place of <see cref="Printer"/>, for the test that runs the service over the real Windows rules and a fake Windows; null for every other test.</summary>
   private readonly IPrinterBackend? _printers;



   /// <summary>
   /// Seeds an enrolled, signed-in agent store with two shared queues, lets the test add its own starting state, then starts the
   /// runtime with a session client wired to this fixture's fake HTTP server and loopback WebSocket. The concurrent-download
   /// limit and (optionally) cached realtime settings are set up front so tests start from a known negotiated state, and an
   /// HTTP fault can be in place before the runtime makes its first request. A test may give the runtime printers of its
   /// own in place of the fake printer; it then shares their queues itself, through the seed.
   /// </summary>
   internal RealtimeRuntimeFixture(int downloads = 1, RealtimeSettings? settings = null, TimeProvider? timeProvider = null, bool cacheSettings = false, string printingVersion = "2.0.0",
      Action<JournalStore>? seed = null, Func<HttpRequestMessage, Exception?>? httpFault = null, IPrinterBackend? printers = null)
   {
      Server = new ScriptedPrintServer { PrintingVersion = printingVersion, HttpFault = httpFault };
      _timeProvider = timeProvider;
      _printers = printers;
      Settings = (settings ?? Settings) with { MaxConcurrentDownloads = downloads };
      using(JournalStore store = TestState.OpenStore(DirectoryPath))
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
      Server.EndClaimSpan();
      _running = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
      Runtime = new SingleServerHarness(DirectoryPath, Logs.Enqueue, _printers ?? Printer, Server.CreateSession, _timeProvider, PipeName, controlResultSaving: () => ControlResultSaving?.Invoke());
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
   /// Has the server accept the agent's WebSocket and answer its HELLO with WELCOME, the desired state and the settings, then
   /// waits until the agent has applied them. With a connected desired state that means the agent reports itself connected;
   /// with a disconnected one, or when the test says traffic stays restricted, it means the transport is up, because ordinary
   /// traffic is then deliberately not allowed. A server channel that dies early fails with a redacted diagnostic instead of
   /// hanging; the only time limit is the fixture's limit for one wait.
   /// </summary>
   internal async Task<ScriptedPrintServer.Connection> ConnectAsync(bool loseSpoolAck = false, DeviceDesiredState? desiredState = null, bool restricted = false)
   {
      DeviceDesiredState desired = desiredState ?? new DeviceDesiredState(1, false, "CONNECTED");
      ScriptedPrintServer.Connection connection = await AcceptAgentAsync(desired, loseSpoolAck);
      bool traffic = !restricted && desired.ConnectionState is "CONNECTED";
      await WaitAsync(() =>
      {
         if(connection.Completion.IsCompleted)
         { throw new InvalidOperationException(Diagnostic("initial realtime state synchronization: server channel stopped")); }
         return traffic ? Runtime.GetSnapshot().Connected : Runtime.GetTransportSnapshot().State is "CONNECTED";
      }, "initial realtime state synchronization");
      return connection;
   }



   /// <summary>Has the server take the agent's next live socket and welcome it, as one wait: an agent that never arrives fails with the fixture's diagnostic instead of hanging.</summary>
   private async Task<ScriptedPrintServer.Connection> AcceptAgentAsync(DeviceDesiredState desired, bool loseSpoolAck)
   {
      using CancellationTokenSource wait = WaitScope();
      try
      { return await Server.AcceptAsync(desired, loseSpoolAck, wait.Token); }
      catch(OperationCanceledException exception) when(wait.IsCancellationRequested)
      { throw new InvalidOperationException(Diagnostic("WebSocket handshake"), exception); }
   }



   /// <summary>Current UTC time in round-trip format, as the server would stamp it.</summary>
   internal static string Now() => ScriptedPrintServer.Now();



   /// <summary>Records a job that the Windows spooler already accepted (spooler id 123, receipt acknowledged) in the given store: the state a cancel command acts on.</summary>
   internal static void SeedSpooledJob(JournalStore store, string id = "job")
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



   /// <summary>
   /// A cancellation source for one wait. It is cancelled when that wait has taken 30 seconds of real time, or when the
   /// fixture is disposed. The limit belongs to the wait: the runtime and its connections are stopped only by disposing the
   /// fixture, so a test made of many waits is not cut off by the time they took together.
   /// </summary>
   private CancellationTokenSource WaitScope()
   {
      var scope = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
      scope.CancelAfter(WaitLimit);
      return scope;
   }



   /// <summary>Polls a condition every 20 ms until it holds, and when this wait reaches its 30-second limit fails with a diagnostic naming the awaited expression.</summary>
   internal async Task WaitAsync(Func<bool> predicate, [CallerArgumentExpression(nameof(predicate))] string stage = "")
   {
      using CancellationTokenSource wait = WaitScope();
      try
      {
         while(!predicate())
         { await Task.Delay(20, wait.Token); }
      }
      catch(OperationCanceledException exception) when(wait.IsCancellationRequested)
      { throw new TimeoutException(Diagnostic(stage), exception); }
   }



   /// <summary>Waits for a signal a test double raises (a held claim arrived, a print started) under the same limit for one wait, failing with the same diagnostic as a timed-out condition.</summary>
   internal async Task WaitAsync(Task signal, [CallerArgumentExpression(nameof(signal))] string stage = "")
   {
      using CancellationTokenSource wait = WaitScope();
      try
      { await signal.WaitAsync(wait.Token); }
      catch(OperationCanceledException exception) when(wait.IsCancellationRequested)
      { throw new TimeoutException(Diagnostic(stage), exception); }
   }



   /// <summary>
   /// Lets the heartbeat loop's current interval run out on a manual clock, so exactly one heartbeat round happens. It waits
   /// until the loop is waiting for its interval, advances the clock by what remains of that wait, and waits until the loop
   /// has started waiting again, by which time that round's frames have been handed to the socket. What remains is the whole
   /// interval unless the test has moved the clock itself since the loop began to wait, which is allowed. The loop's wait is
   /// told from other waits on the clock (the update preparation limit, the reconnect backoff) by its length.
   /// </summary>
   internal async Task HeartbeatAsync(ManualTimeProvider clock)
   {
      var interval = TimeSpan.FromSeconds(Settings.HeartbeatIntervalSeconds);
      TimeSpan? remaining = null;
      await WaitAsync(() => (remaining = clock.RemainingOfWaitArmedFor(interval)) is not null, "heartbeat loop waiting");
      clock.Advance(remaining!.Value);
      await WaitAsync(() => clock.HasTimerDueIn(interval), "heartbeat loop waiting again");
   }



   /// <summary>
   /// Lets the connection loop's backoff run out on a manual clock and waits until the loop has made its next pass and is
   /// backing off again. While no connection is up the backoff is the only wait on the clock, so a pending wait means the
   /// loop has finished a pass: it evaluated authorization and connected, or decided not to.
   /// </summary>
   internal async Task BackoffPassAsync(ManualTimeProvider clock)
   {
      await WaitAsync(() => clock.PendingTimers > 0, "connection loop backing off");
      clock.Advance(TimeSpan.FromMilliseconds(Settings.ReconnectMaxDelayMillis));
      await WaitAsync(() => clock.PendingTimers > 0, "connection loop backing off again");
   }



   /// <summary>Sends one command over the runtime's management pipe, as the companion and command line do, and returns the reply; the exchange is one wait under the fixture's limit.</summary>
   internal async Task<string> PipeAsync(PipeRequest request)
   {
      using CancellationTokenSource wait = WaitScope();
      return await Runtime.PipeAsync(request, wait.Token);
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
      using JournalStore store = TestState.OpenStore(DirectoryPath);
      return store.GetState(id);
   }



   /// <summary>Stops the runtime and every server connection, releasing anything a test left held, then removes the temporary store.</summary>
   public async ValueTask DisposeAsync()
   {
      await _stopping.CancelAsync();
      Printer.Release.TrySetResult();
      await Server.CloseConnectionsAsync();
      await StopAsync();
      await Server.DisposeAsync();
      _stopping.Dispose();
      JournalFiles.Delete(DirectoryPath);
   }
}
