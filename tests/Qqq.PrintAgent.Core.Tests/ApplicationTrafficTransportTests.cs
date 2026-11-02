/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

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

/// <summary>
/// The traffic counter shown in the footer must count real application payload bytes as they move, not buffered or queued data
/// and not protocol overhead. These tests drive a document download and a WebSocket exchange through the real clients and compare
/// the counter with exact byte counts.
/// </summary>
public sealed class ApplicationTrafficTransportTests
{
   /// <summary>
   /// A document download counts bytes as each chunk is read: while the stream is paused only the first 1,024 bytes are counted,
   /// not the unread remainder, and cancelling leaves that count and an empty staging folder. A completed download counts the full
   /// length, and uploads never change during the download.
   /// </summary>
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



   /// <summary>
   /// Over a WebSocket, nothing is counted when a frame is merely queued or during the handshake; an outbound message is counted
   /// once actually sent, at its exact UTF-8 size. An inbound message split across two frames, with a multi-byte character, is
   /// counted once at its full byte length.
   /// </summary>
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



   /// <summary>Fake native QQQ server that tallies request-body and JSON-response bytes itself, giving the test an independent total to compare with the meter, and serves the supplied document stream for content requests.</summary>
   private sealed class NativeHandler(Stream document) : HttpMessageHandler
   {
      internal long Uploaded { get; private set; }
      internal long Downloaded { get; private set; }



      /// <summary>Adds the request body to the upload tally, serves discovery, login and identity JSON (adding its size to the download tally), or returns the document body.</summary>
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



   /// <summary>A forward-only document body delivered in 1,024-byte reads that pauses after the first chunk until released, so a test can inspect the counter mid-download.</summary>
   private sealed class PausedDocument(byte[] bytes) : MemoryStream(bytes)
   {
      internal TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);



      /// <summary>Not seekable, as a network body is not.</summary>
      public override bool CanSeek => false;



      /// <summary>Reads at most 1,024 bytes; after the first 1,024 it signals the pause and waits for release before continuing.</summary>
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
