using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Testing;

namespace Qqq.PrintAgent.Core.Tests;

public sealed class RealtimeTransportTests
{
   [Theory]
   [InlineData("1.0.0")]
   [InlineData("1.1.0")]
   [InlineData("3.0.0")]
   public async Task RealtimeRequiresSupportedNativeExtensionMajor(string extensionVersion)
   {
      using var peer = new LoopbackWebSocketPeer();
      using var body = new MemoryStream();
      using var handler = new NativeHandler(body, extensionVersion);
      using var session = new QqqSessionClient(peer.Origin, handler, allowLoopbackHttp: true);
      QqqSessionException exception = await Assert.ThrowsAsync<QqqSessionException>(async () =>
      {
         await session.SignInWithPasswordAsync("synthetic-user", "synthetic-password");
         await session.BindDeviceAsync(new QqqDeviceCredential("device", "synthetic-proof"));
         using WebSocket socket = await session.ConnectPrintingAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
      });
      Assert.Equal(QqqSessionException.FailureKind.ConfigurationRequired, exception.Failure);
   }

   [Fact]
   public async Task PdfLargerThanLegacyTwentyMiBLimitStreamsAndVerifiesSuccessfully()
   {
      byte[] bytes = new byte[25 * 1024 * 1024];
      Random.Shared.NextBytes(bytes);
      using var handler = new DocumentHandler(bytes);
      using var api = new PrintApiClient("https://synthetic.invalid", handler: handler);
      string staging = Path.Combine(Path.GetTempPath(), "qqq-large-stream-" + Guid.NewGuid().ToString("N"));
      PrintJob job = Job(bytes.Length) with { ContentSha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
      try
      {
         using DownloadedDocument downloaded = await api.DownloadFileAsync(job, staging, new RealtimeSettings(), CancellationToken.None);
         Assert.Equal(bytes.Length, new FileInfo(downloaded.Path).Length);
         using FileStream file = File.OpenRead(downloaded.Path);
         Assert.Equal(job.ContentSha256, Convert.ToHexString(await SHA256.HashDataAsync(file)));
      }
      finally { Directory.Delete(staging, recursive: true); }
   }

   [Fact]
   public async Task NativeCookieAndDeviceProofUpgradeKeepsReceivingDuringStalledDocument()
   {
      using var peer = new LoopbackWebSocketPeer();
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
      using var body = new BlockingDocument();
      using var handler = new NativeHandler(body);
      using var session = new QqqSessionClient(peer.Origin, handler, allowLoopbackHttp: true);
      await session.SignInWithPasswordAsync("synthetic-user", "synthetic-password", timeout.Token);
      await session.BindDeviceAsync(new QqqDeviceCredential("device", "synthetic-proof"), cancellationToken: timeout.Token);
      Task<WebSocket> accepted = peer.AcceptAsync(timeout.Token);
      using WebSocket socket = await session.ConnectPrintingAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), timeout.Token);
      using WebSocket server = await accepted;
      Assert.Contains("sessionUUID=synthetic-session", peer.Headers["Cookie"], StringComparison.Ordinal);
      Assert.Equal("synthetic-proof", peer.Headers["X-QQQ-Device-Token"]);
      Assert.Equal(QqqSessionClient.ClientChannel, peer.Headers["X-QQQ-Client"]);
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      var state = new TaskCompletionSource<RealtimeEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
      Task reader = channel.RunAsync(message => state.TrySetResult(message), timeout.Token);
      using PrintApiClient api = PrintApiClient.ForSession(session).WithRealtime(channel, new RealtimeSettings());
      string staging = Path.Combine(Path.GetTempPath(), "qqq-realtime-test-" + Guid.NewGuid().ToString("N"));
      Task<DownloadedDocument> download = api.DownloadFileAsync(Job(25 * 1024 * 1024), staging, new RealtimeSettings(), timeout.Token);
      try
      {
         await body.Entered.Task.WaitAsync(timeout.Token);
         await LoopbackWebSocketPeer.SendAsync(server, "DESIRED_STATE", new DeviceDesiredState(7, true, "CONNECTED"), timeout.Token);
         Assert.Equal("DESIRED_STATE", (await state.Task.WaitAsync(TimeSpan.FromSeconds(2), timeout.Token)).Type);
         Assert.False(download.IsCompleted);
         await timeout.CancelAsync();
         await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
         Assert.Empty(Directory.EnumerateFiles(staging));
      }
      finally
      {
         await timeout.CancelAsync();
         try
         { await reader; }
         catch(Exception exception) when(exception is OperationCanceledException or WebSocketException) { }
         Directory.Delete(staging, recursive: true);
      }
   }

   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task DiskStreamingVerifiesLengthAndHashAndDeletesPartialFiles(bool corrupt)
   {
      byte[] bytes = Encoding.ASCII.GetBytes("%PDF-synthetic-content");
      using var handler = new DocumentHandler(bytes);
      using var api = new PrintApiClient("https://synthetic.invalid", handler: handler);
      string staging = Path.Combine(Path.GetTempPath(), "qqq-download-test-" + Guid.NewGuid().ToString("N"));
      PrintJob job = Job(bytes.Length) with { ContentSha256 = Convert.ToHexString(SHA256.HashData(corrupt ? [1] : bytes)) };
      try
      {
         if(corrupt)
         { await Assert.ThrowsAsync<InvalidDataException>(() => api.DownloadFileAsync(job, staging, new RealtimeSettings(), CancellationToken.None)); }
         else
         {
            using DownloadedDocument downloaded = await api.DownloadFileAsync(job, staging, new RealtimeSettings(), CancellationToken.None);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(downloaded.Path));
            Assert.Throws<IOException>(() => { using var write = new FileStream(downloaded.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
         }
         Assert.Empty(Directory.EnumerateFiles(staging));
      }
      finally { if(Directory.Exists(staging)) { Directory.Delete(staging, recursive: true); } }
   }

   private static PrintJob Job(long length) => new("job", "printer", "Synthetic", "PDF", new string('0', 64), length, new PrintOptions(), "", "CLAIMED");

   private sealed class DocumentHandler(byte[] bytes) : HttpMessageHandler
   {
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
         => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
   }

   private sealed class NativeHandler(Stream content, string extensionVersion = "2.0.0") : HttpMessageHandler
   {
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         string path = request.RequestUri!.AbsolutePath;
         if(path.EndsWith("/content", StringComparison.Ordinal))
         { return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(content) }); }
         string body = path switch
         {
            "/qqq/v1/version" => """{"apiVersion":"1.9.0","features":["version","metaData","processes","processCancel","extensions","widgets","nativeSessionLogin"],"clientChannels":["api"],"extensions":[{"name":"printing","version":"2.0.0","pathPrefix":"/qbit/printing"}]}""",
            "/qqq/v1/metaData/authentication" => """{"name":"local","type":"TABLE_BASED"}""",
            "/qqq/v1/manageSession" => """{"uuid":"synthetic-session"}""",
            "/qqq/v1/qbit/printing/session" => """{"principalId":"user","tenantId":"tenant","deviceId":"device"}""",
            _ => throw new InvalidOperationException("Unexpected HTTP route."),
         };
         return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.Replace("2.0.0", extensionVersion, StringComparison.Ordinal)) });
      }
   }

   private sealed class BlockingDocument : Stream
   {
      internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      public override bool CanRead => true;
      public override bool CanSeek => false;
      public override bool CanWrite => false;
      public override long Length => throw new NotSupportedException();
      public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
      public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
      {
         Entered.TrySetResult();
         await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
         return 0;
      }
      public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
      public override void Flush() => throw new NotSupportedException();
      public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
      public override void SetLength(long value) => throw new NotSupportedException();
      public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
   }
}
