using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Testing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

internal sealed class RealtimeRuntimeFixture : IAsyncDisposable
{
   internal LoopbackWebSocketPeer Peer { get; } = new();
   internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "qqq-native-runtime-" + Guid.NewGuid().ToString("N"));
   internal RealtimeSettings Settings { get; set; } = new(Revision: 1, ReconnectMinDelayMillis: 100, ReconnectMaxDelayMillis: 1000, ReconnectJitterPercent: 0, AckTimeoutMillis: 1000);
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
   private readonly Lock _claimLock = new();
   private PrintJob? _claimed;
   private readonly CancellationTokenSource _stopping = new(TimeSpan.FromSeconds(30));
   private readonly Task _run;
   private readonly List<Connection> _connections = [];

   internal RealtimeRuntimeFixture(int downloads = 1, RealtimeSettings? settings = null, TimeProvider? timeProvider = null)
   {
      Settings = (settings ?? Settings) with { MaxConcurrentDownloads = downloads };
      using(var store = new AgentStore(DirectoryPath))
      {
         store.SaveConfiguration(new AgentConfiguration(Peer.Origin.AbsoluteUri, "device", "synthetic-proof"));
         store.SaveAuthentication(new AgentAuthentication(new QqqSessionBinding(Peer.Origin, "user", "tenant", "device"), new QqqSessionCredential(Peer.Origin, "synthetic-session")));
         store.SetExposure("printer", true, Printer.Discover());
         store.SetExposure("other", true, Printer.Discover());
      }
      string pipe = "qqq-realtime-fixture-" + Guid.NewGuid().ToString("N");
      Runtime = new AgentRuntime(DirectoryPath, Logs.Enqueue, Printer, pipeName: pipe,
         pipeFactory: () => new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly),
         sessionFactory: origin => new QqqSessionClient(origin, new Handler(this), allowLoopbackHttp: true), timeProvider: timeProvider);
      _run = Runtime.RunAsync(_stopping.Token);
   }

   internal async Task<Connection> ConnectAsync(bool loseSpoolAck = false)
   {
      WebSocket socket;
      try
      { socket = await Peer.AcceptAsync(_stopping.Token).WaitAsync(TimeSpan.FromSeconds(5)); }
      catch(TimeoutException exception) { throw new InvalidOperationException("Synthetic runtime failed to connect: " + string.Join("; ", Logs), exception); }
      var connection = new Connection(socket, this, loseSpoolAck, _stopping.Token);
      _connections.Add(connection);
      RealtimeEnvelope hello = await LoopbackWebSocketPeer.ReceiveAsync(socket, _stopping.Token);
      Assert.Equal("HELLO", hello.Type);
      LastHelloSettingsRevision = hello.Payload.GetProperty("lastSettingsRevision").GetInt64();
      await connection.SendAsync("WELCOME", new { connectionId = Guid.NewGuid().ToString("N"), serverTime = Now(), desiredState = new DeviceDesiredState(1, false, "CONNECTED"), settings = Settings });
      connection.Start();
      await WaitAsync(() => Runtime.GetSnapshot().Connected);
      return connection;
   }

   internal static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

   internal void AddJob(string id, Func<Stream>? content = null, string printerId = "printer", long? length = null)
   {
      byte[] bytes = Encoding.ASCII.GetBytes("%PDF-synthetic-" + id);
      Documents[id] = content ?? (() => new MemoryStream(bytes));
      Queue.Enqueue(new PrintJob(id, printerId, "Synthetic " + id, "PDF", Convert.ToHexString(SHA256.HashData(bytes)), length ?? (content is null ? bytes.Length : 25 * 1024 * 1024),
         new PrintOptions(), Now(), "QUEUED"));
   }

   internal async Task WaitAsync(Func<bool> predicate)
   {
      while(!predicate())
      { await Task.Delay(20, _stopping.Token); }
   }

   internal string? State(string id)
   {
      using var store = new AgentStore(DirectoryPath);
      return store.GetState(id);
   }

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

   internal sealed class SyntheticFilePrinter : IFilePrinterBackend
   {
      internal int Prints;
      internal TimeSpan LastSubmissionTimeout;
      internal ConcurrentQueue<string> Order { get; } = new();
      public IReadOnlyList<PrinterInfo> Discover() => [new("printer", "Synthetic", "Fixture", "None", "READY", ["PDF", "ZPL"]), new("other", "Other synthetic", "Fixture", "None", "READY", ["PDF", "ZPL"])];
      public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken) => throw new InvalidOperationException("The native realtime path must use a file.");
      public Task<string> PrintFileAsync(PrintJob job, string path, CancellationToken cancellationToken)
      {
         Assert.Equal(job.ContentLength, new FileInfo(path).Length);
         Interlocked.Increment(ref Prints);
         Order.Enqueue(job.JobId);
         return Task.FromResult("123");
      }
      public Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken)
      {
         LastSubmissionTimeout = submissionTimeout;
         return PrintFileAsync(job, path, cancellationToken);
      }
   }

   internal sealed class Connection(WebSocket socket, RealtimeRuntimeFixture fixture, bool loseSpoolAck, CancellationToken token) : IAsyncDisposable
   {
      private readonly SemaphoreSlim _send = new(1, 1);
      private Task _pump = Task.CompletedTask;
      internal void Start() => _pump = PumpAsync();
      internal TaskCompletionSource LostSpoolAck { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      internal async Task SendAsync(string type, object payload)
      {
         await _send.WaitAsync(token);
         try
         { await LoopbackWebSocketPeer.SendAsync(socket, type, payload, token); }
         finally { _send.Release(); }
      }
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

   private sealed class Handler(RealtimeRuntimeFixture fixture) : HttpMessageHandler
   {
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         string path = request.RequestUri!.AbsolutePath;
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
            "/qqq/v1/version" => """{"apiVersion":"1.9.0","features":["version","metaData","processes","processCancel","extensions","widgets","nativeSessionLogin"],"clientChannels":["api"],"extensions":[{"name":"printing","version":"2.0.0","pathPrefix":"/qbit/printing"}]}""",
            "/qqq/v1/manageSession" => """{"uuid":"synthetic-session"}""",
            "/qqq/v1/metaData/authentication" => """{"name":"local","type":"TABLE_BASED"}""",
            "/qqq/v1/qbit/printing/session" => """{"principalId":"user","tenantId":"tenant","deviceId":"device"}""",
            _ => Unexpected(path),
         });
      }
      private string Unexpected(string path)
      {
         Interlocked.Increment(ref fixture.ForbiddenPolls);
         throw new InvalidOperationException("Unexpected HTTP polling/control route: " + path);
      }
      private static Task<HttpResponseMessage> Response(string value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(value) });
   }
}
