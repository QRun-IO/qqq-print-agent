/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Testing;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// One scripted QQQ server with the printing extension, as the service sees it: a loopback WebSocket listener for the realtime
/// channel, an HTTP handler behind the session client, a queue of synthetic jobs with their documents, and a record of what
/// the agent sent. A test drives what the server says (settings, state, commands, acknowledgements) and reads back what
/// reached it. The server knows nothing about the service it answers: a fixture joins it to a runtime, and waits for the
/// runtime's side of a connection. Everything the server holds belongs to the instance, so several can stand beside one
/// service, each at its own loopback address, without one seeing another's jobs, messages, counters or holds. The other parts
/// of this class hold the HTTP side and one accepted realtime connection.
/// </summary>
internal sealed partial class ScriptedPrintServer : IAsyncDisposable
{
   /// <summary>The loopback listener the agent's WebSocket connects to.</summary>
   internal LoopbackWebSocketPeer Peer { get; } = new();



   /// <summary>The server's address: the origin the agent is paired with and signs in to, and the one its session client is made for.</summary>
   internal Uri Origin => Peer.Origin;



   /// <summary>The realtime settings the server sends in WELCOME: short reconnect delays without jitter, everything else at its default.</summary>
   internal RealtimeSettings Settings { get; set; } = new(Revision: 1, ReconnectMinDelayMillis: 100, ReconnectMaxDelayMillis: 1000, ReconnectJitterPercent: 0);



   /// <summary>The server's queue of jobs waiting to be claimed, oldest first.</summary>
   internal ConcurrentQueue<PrintJob> Queue { get; } = new();



   /// <summary>How each job's document is served when the agent downloads it, by job id.</summary>
   internal ConcurrentDictionary<string, Func<Stream>> Documents { get; } = new(StringComparer.Ordinal);



   /// <summary>Every message the agent sent on any connection to this server, in the order the server received them.</summary>
   internal ConcurrentQueue<RealtimeEnvelope> Messages { get; } = new();



   /// <summary>When set, the server holds a RECEIVED event (and so the claimed job) until the test completes it.</summary>
   internal TaskCompletionSource? ReceivedRelease { get; set; }



   /// <summary>When set, the server holds its acknowledgement of STATE_APPLIED until the test completes it, which keeps the agent from becoming ready.</summary>
   internal TaskCompletionSource? StateAppliedRelease { get; set; }



   /// <summary>Runs for every agent message before the server answers it; a test awaits inside it to hold one acknowledgement back.</summary>
   internal Func<RealtimeEnvelope, Task>? BeforeAcknowledgement { get; set; }



   /// <summary>The settings revision the agent reported in its latest HELLO.</summary>
   internal long LastHelloSettingsRevision { get; private set; }



   /// <summary>Returns an error code to answer an agent message with a realtime ERROR frame instead of an acknowledgement (NOT_FOUND models a server that no longer knows the record), or null to acknowledge it.</summary>
   internal Func<RealtimeEnvelope, string?>? RefuseWith { get; set; }



   /// <summary>When true, the server closes the socket with a close frame straight after refusing a message, as a server that gives up on a misbehaving client does.</summary>
   internal bool CloseAfterRefusal { get; set; }



   /// <summary>Returns true for an agent message the server must receive and then drop the connection without acknowledging, as a network failure at that exact moment would.</summary>
   internal Func<RealtimeEnvelope, bool>? LoseAcknowledgementOf { get; set; }



   /// <summary>
   /// When true the server behaves as a real one does when the agent reports an older desired-state revision as applied than
   /// the newest one it was sent: it sends that desired state again. When false it stays silent, which shows what a lost
   /// frame costs when nothing repairs it.
   /// </summary>
   internal bool ResendUnappliedState { get; set; }



   /// <summary>Captures the actual HELLO payload so compatibility tests observe the wire rather than duplicating capability selection.</summary>
   internal string[] LastHelloCapabilities { get; private set; } = [];

   private readonly Lock _claimLock = new();
   private PrintJob? _claimed;
   private readonly CancellationTokenSource _stopping = new();
   private readonly List<Connection> _connections = [];
   private bool _closed;



   /// <summary>
   /// A session client for this server: the real client the service uses, with its HTTP sent to this server's scripted
   /// handler and its WebSocket to this server's loopback listener. A service's session factory calls this for the origin
   /// it was asked to reach, so a fixture with several servers hands each origin to the server that owns it.
   /// </summary>
   internal IQqqSessionClient CreateSession(Uri origin) => new QqqSessionClient(origin, CreateHandler(), allowLoopbackHttp: true);



   /// <summary>The scripted HTTP side on its own, for a caller that speaks HTTP to this server without a session client.</summary>
   internal HttpMessageHandler CreateHandler() => new Handler(this);



   /// <summary>
   /// Accepts the agent's WebSocket, requires HELLO first, records its settings revision and capabilities, answers WELCOME
   /// with the given desired state and this server's settings, and starts answering the agent on that connection. It returns
   /// once WELCOME is sent; whether the agent has applied it is the caller's to wait for, because only the caller can see the
   /// agent. The token limits the wait for the socket and its HELLO.
   /// </summary>
   internal async Task<Connection> AcceptAsync(DeviceDesiredState desired, bool loseSpoolAck, CancellationToken token)
   {
      (WebSocket socket, RealtimeEnvelope hello) = await AcceptLiveSocketAsync(token);
      var connection = new Connection(socket, this, loseSpoolAck, _stopping.Token);
      _connections.Add(connection);
      Assert.Equal("HELLO", hello.Type);
      LastHelloSettingsRevision = hello.Payload.GetProperty("lastSettingsRevision").GetInt64();
      LastHelloCapabilities = hello.Payload.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()!).ToArray();
      await connection.SendAsync("WELCOME", new { connectionId = Guid.NewGuid().ToString("N"), serverTime = Now(), desiredState = desired, settings = Settings });
      connection.Start();
      return connection;
   }



   /// <summary>
   /// Accepts the agent's next live socket and reads its first message. An attempt the agent abandoned while the upgrade was
   /// in flight (the service was restarted, or told to reconnect) is still in the listener's queue with nobody behind it; such
   /// a socket fails before any message arrives, and the live attempt queued after it is taken instead.
   /// </summary>
   private async Task<(WebSocket Socket, RealtimeEnvelope Hello)> AcceptLiveSocketAsync(CancellationToken token)
   {
      while(true)
      {
         WebSocket? socket = null;
         try
         {
            socket = await Peer.AcceptAsync(token);
            RealtimeEnvelope hello = await LoopbackWebSocketPeer.ReceiveAsync(socket, token);
            // From this HELLO until the agent's report of the applied state is acknowledged, no claim is legitimate.
            _claimsAllowed = false;
            return (socket, hello);
         }
         catch(Exception exception) when(exception is IOException or WebSocketException)
         { socket?.Dispose(); }
      }
   }



   /// <summary>
   /// Ends the span in which claims are legitimate, as a restart of the service does: what the new service knows is what the
   /// old one saved, and that never includes a connection whose applied state this server acknowledged.
   /// </summary>
   internal void EndClaimSpan() => _claimsAllowed = false;



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



   /// <summary>How many messages of one type the agent has sent to this server so far.</summary>
   internal int Count(string type) => Messages.Count(message => string.Equals(message.Type, type, StringComparison.Ordinal));



   /// <summary>
   /// Stops answering: cancels every connection's pump, lets go of a claim a test left held, and closes the accepted sockets.
   /// The listener stays up until the server is disposed, so a fixture can close the connections, stop its service, and
   /// only then take the address away, which is the order the suite has always stopped in. Safe to call more than once.
   /// </summary>
   internal async Task CloseConnectionsAsync()
   {
      if(_closed)
      { return; }
      _closed = true;
      await _stopping.CancelAsync();
      ClaimHold?.TrySetResult();
      foreach(Connection connection in _connections)
      { await connection.DisposeAsync(); }
   }



   /// <summary>Closes the connections if they are still open, then stops the listener.</summary>
   public async ValueTask DisposeAsync()
   {
      await CloseConnectionsAsync();
      Peer.Dispose();
      _stopping.Dispose();
   }
}
