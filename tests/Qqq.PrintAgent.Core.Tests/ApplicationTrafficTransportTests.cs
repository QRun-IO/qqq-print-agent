using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Core.Transport;
using Qqq.PrintAgent.Testing;

namespace Qqq.PrintAgent.Core.Tests;

public sealed class ApplicationTrafficTransportTests
{
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task NativeDocumentCountsIncrementallyWithoutBufferingOrCountingUnreadRemainder(bool cancel)
   {
      byte[] document = Encoding.UTF8.GetBytes("%PDF-" + new string('é', 16_384));
      var meter = new ApplicationTrafficMeter();
      using var content = new PausedDocument(document);
      using var handler = new NativeHandler(content);
      using var session = new QqqSessionClient(new Uri("https://synthetic.invalid"), handler, traffic: meter);
      await session.SignInWithPasswordAsync("synthetic", "synthetic-password");
      await session.BindDeviceAsync(new QqqDeviceCredential("device", "synthetic-proof"));
      ApplicationTrafficSnapshot before = meter.GetSnapshot();
      Assert.Equal(handler.Uploaded, before.UploadedBytes);
      Assert.Equal(handler.Downloaded, before.DownloadedBytes);
      Assert.True(before.UploadedBytes > 0);
      using var api = PrintApiClient.ForSession(session);
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
      string staging = Path.Combine(Path.GetTempPath(), "qqq-traffic-" + Guid.NewGuid().ToString("N"));
      var job = new PrintJob("job", "printer", "Synthetic", "PDF", Convert.ToHexString(SHA256.HashData(document)), document.Length, new PrintOptions(), "", "CLAIMED");
      Task<DownloadedDocument> download = api.DownloadFileAsync(job, staging, new RealtimeSettings(), timeout.Token);
      try
      {
         await content.Paused.Task.WaitAsync(timeout.Token);
         Assert.False(download.IsCompleted);
         Assert.Equal(before.DownloadedBytes + 1024, meter.GetSnapshot().DownloadedBytes);
         if(cancel)
         {
            await timeout.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
            Assert.Equal(before.DownloadedBytes + 1024, meter.GetSnapshot().DownloadedBytes);
            Assert.Empty(Directory.EnumerateFiles(staging));
         }
         else
         {
            content.Release.TrySetResult();
            using DownloadedDocument downloaded = await download;
            Assert.Equal(document.Length, new FileInfo(downloaded.Path).Length);
            Assert.Equal(before.DownloadedBytes + document.Length, meter.GetSnapshot().DownloadedBytes);
         }
         Assert.Equal(before.UploadedBytes, meter.GetSnapshot().UploadedBytes);
      }
      finally
      {
         await timeout.CancelAsync();
         if(Directory.Exists(staging))
         { Directory.Delete(staging, recursive: true); }
      }
   }

   [Fact]
   public async Task WebSocketCountsSentAndFragmentedUtf8PayloadsWithoutCountingQueuedFramesOrHandshake()
   {
      using var peer = new LoopbackWebSocketPeer();
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
      using var client = new ClientWebSocket();
      client.Options.AddSubProtocol("qqq-printing.v1");
      Task<WebSocket> accepting = peer.AcceptAsync(timeout.Token);
      var address = new UriBuilder(new Uri(peer.Origin, "/qqq/v1/qbit/printing/device/realtime")) { Scheme = "ws" };
      await client.ConnectAsync(address.Uri, timeout.Token);
      using WebSocket server = await accepting;
      var meter = new ApplicationTrafficMeter();
      using var channel = new PrintingRealtimeChannel(client, new RealtimeSettings(), meter);
      await channel.SendAsync("PING", new { }, "synthetic-message", token: timeout.Token);
      Assert.Equal(0, meter.GetSnapshot().UploadedBytes);
      Assert.Equal(0, meter.GetSnapshot().DownloadedBytes);
      var received = new TaskCompletionSource<RealtimeEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
      Task run = channel.RunAsync(envelope => received.TrySetResult(envelope), timeout.Token);
      try
      {
         RealtimeEnvelope outbound = await LoopbackWebSocketPeer.ReceiveAsync(server, timeout.Token);
         Assert.Equal("PING", outbound.Type);
         int expectedSent = JsonSerializer.SerializeToUtf8Bytes(new { type = "PING", messageId = "synthetic-message", payload = new { } }, PrintApiClient.JsonOptions).Length;
         while(meter.GetSnapshot().UploadedBytes == 0)
         { await Task.Delay(1, timeout.Token); }
         Assert.Equal(expectedSent, meter.GetSnapshot().UploadedBytes);
         byte[] incoming = Encoding.UTF8.GetBytes("""{"type":"PONG","messageId":"fragmented","payload":{"label":"é"}}""");
         await server.SendAsync(incoming.AsMemory(0, 9), WebSocketMessageType.Text, false, timeout.Token);
         await server.SendAsync(incoming.AsMemory(9), WebSocketMessageType.Text, true, timeout.Token);
         Assert.Equal("PONG", (await received.Task.WaitAsync(timeout.Token)).Type);
         Assert.Equal(incoming.Length, meter.GetSnapshot().DownloadedBytes);
      }
      finally
      {
         await timeout.CancelAsync();
         try
         { await run; }
         catch(Exception exception) when(exception is OperationCanceledException or WebSocketException) { }
      }
   }

   private sealed class NativeHandler(Stream document) : HttpMessageHandler
   {
      internal long Uploaded { get; private set; }
      internal long Downloaded { get; private set; }
      protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         if(request.Content is not null)
         {
            byte[] body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Uploaded += body.Length;
         }
         string path = request.RequestUri!.AbsolutePath;
         if(path.EndsWith("/content", StringComparison.Ordinal))
         { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(document) }; }
         string json = path switch
         {
            "/qqq/v1/version" => """{"apiVersion":"1.9.0","features":["version","metaData","processes","processCancel","extensions","widgets","nativeSessionLogin"],"clientChannels":["api"],"extensions":[{"name":"printing","version":"2.0.0","pathPrefix":"/qbit/printing"}]}""",
            "/qqq/v1/metaData/authentication" => """{"name":"local","type":"TABLE_BASED"}""",
            "/qqq/v1/manageSession" => """{"uuid":"synthetic-session"}""",
            "/qqq/v1/qbit/printing/session" => """{"principalId":"user","tenantId":"tenant","deviceId":"device"}""",
            _ => throw new InvalidOperationException("Unexpected HTTP route."),
         };
         Downloaded += Encoding.UTF8.GetByteCount(json);
         return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
      }
   }

   private sealed class PausedDocument(byte[] bytes) : MemoryStream(bytes)
   {
      internal TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      public override bool CanSeek => false;
      public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
      {
         if(Position >= 1024)
         {
            Paused.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
         }
         return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 1024)], cancellationToken);
      }
   }
}
