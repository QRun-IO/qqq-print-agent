/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Testing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// The realtime transport carries job notifications and receipts over a WebSocket that the session client opens with the QQQ
/// session cookie plus device proof. These tests cover when that connection may be opened, that large documents stream to disk
/// with length and hash verification, and that a stalled download never blocks incoming messages.
/// </summary>
public sealed class RealtimeTransportTests
{
   /// <summary>The printing extension must be in a supported major version (2.x) before a realtime connection is opened; 1.x or 3.x fail with a configuration error instead of connecting to a server whose protocol may differ.</summary>
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



   /// <summary>A 25 MiB PDF, larger than the old 20 MiB in-memory limit, is streamed to a staging file and its full SHA-256 verifies, so big documents work without being held in memory.</summary>
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



   /// <summary>
   /// The WebSocket upgrade must carry the session cookie, the device proof and the client channel. While a document download
   /// is stuck mid-body, a server message still reaches the channel within two seconds, so a slow download cannot hide a pause or
   /// cancel command. Cancelling then aborts the download and leaves nothing in the staging folder.
   /// </summary>
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



   /// <summary>
   /// A download with matching bytes leaves one file that is held open against writers; a download whose hash does not match
   /// throws and leaves no file. Either way the staging folder never holds a partial or unverified document.
   /// </summary>
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



   /// <summary>A synthetic PDF job of the given declared length; tests set the hash when they need a real one.</summary>
   private static PrintJob Job(long length) => new("job", "printer", "Synthetic", "PDF", new string('0', 64), length, new PrintOptions(), "", "CLAIMED");



   /// <summary>Fake server that answers every request with the same document bytes.</summary>
   private sealed class DocumentHandler(byte[] bytes) : HttpMessageHandler
   {
      /// <summary>Returns 200 with the configured bytes whatever the URL.</summary>
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
         => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
   }



   /// <summary>Fake native QQQ server: discovery (advertising the given printing extension version), table-based login, session identity and a document body supplied by the test. Any other route is a test failure.</summary>
   private sealed class NativeHandler(Stream content, string extensionVersion = "2.0.0") : HttpMessageHandler
   {
      /// <summary>Serves the scripted discovery, authentication and identity responses, or the content stream for a content URL.</summary>
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



   /// <summary>A read-only stream whose first read signals the test and then never completes until cancelled, simulating a server that stalls mid-document.</summary>
   private sealed class BlockingDocument : Stream
   {
      internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);



      /// <summary>Reports the stream as readable, which the download code requires before it will try to read the body.</summary>
      public override bool CanRead => true;



      /// <summary>Not seekable, as a network body is not.</summary>
      public override bool CanSeek => false;



      /// <summary>Reports the stream as not writable, as a downloaded network body is read-only.</summary>
      public override bool CanWrite => false;



      /// <summary>Unknown length, as for a chunked response.</summary>
      public override long Length => throw new NotSupportedException();



      /// <summary>Position is unsupported on a forward-only network body.</summary>
      public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }



      /// <summary>Signals that the download reached the body, then waits forever (until the token cancels).</summary>
      public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
      {
         Entered.TrySetResult();
         await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
         return 0;
      }



      /// <summary>Synchronous reads are unsupported; the download must be asynchronous.</summary>
      public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();



      /// <summary>Throws, because a read-only stalled body has nothing to flush and the download must never try.</summary>
      public override void Flush() => throw new NotSupportedException();



      /// <summary>Throws, because a forward-only network body cannot be repositioned and the download must read it once in order.</summary>
      public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();



      /// <summary>Throws, because the length of a streamed body cannot be set by the reader.</summary>
      public override void SetLength(long value) => throw new NotSupportedException();



      /// <summary>Throws, because the body is read-only and nothing may be written back into it.</summary>
      public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
   }
}
