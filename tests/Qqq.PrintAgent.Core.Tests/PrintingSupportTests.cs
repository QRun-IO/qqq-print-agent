/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Core.Transport;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// The smaller pieces the print path is built from: the exposure filter, the file-backend contract, staged-file cleanup,
/// the settings parser, the realtime channel's own limits, and the wrappers that count traffic or keep a socket's
/// transport alive. Each is tested directly, with fakes and per-test temporary files only.
/// </summary>
public sealed class PrintingSupportTests
{
   private static readonly PrintJob Job = FakePrintServer.Job([1, 2, 3]);



   /// <summary>Only printers the operator shares, and that have a usable format, are offered to the server; the shared set is read afresh each time, so unsharing takes effect at once.</summary>
   [Fact]
   public void ExposureFilterOffersOnlySharedPrintersWithAFormat()
   {
      var inner = new FakePrinterBackend();
      inner.Printers.Add(new PrinterInfo("no-formats", "Print to file", "Driver", "FILE:", "READY", []));
      inner.Printers.Add(new PrinterInfo("unshared", "Back office", "Driver", "USB002", "READY", ["PDF"]));
      var shared = new HashSet<string>(StringComparer.Ordinal) { "printer1", "no-formats" };
      var backend = new ExposedPrinterBackend(inner, () => shared);
      Assert.Equal("printer1", Assert.Single(backend.Discover()).PrinterId);
      shared.Clear();
      Assert.Empty(backend.Discover());
   }



   /// <summary>A job is submitted only while its printer is still shared: the check is made again at submission, the staged file and its submission limit reach the inner backend unchanged, and a refusal never reaches it.</summary>
   [Fact]
   public async Task ExposureFilterRechecksSharingAtSubmission()
   {
      using var temp = new TempDirectory();
      await File.WriteAllBytesAsync(temp.File("staged.document"), [9, 8, 7]);
      var inner = new FakePrinterBackend();
      var shared = new HashSet<string>(StringComparer.Ordinal) { "printer1" };
      var backend = new ExposedPrinterBackend(inner, () => shared);

      Assert.Equal("spool-1", await backend.PrintFileAsync(Job, temp.File("staged.document"), TimeSpan.FromSeconds(30), CancellationToken.None));
      (byte[] content, TimeSpan limit) = Assert.Single(inner.Files);
      Assert.Equal(new byte[] { 9, 8, 7 }, content);
      Assert.Equal(TimeSpan.FromSeconds(30), limit);

      shared.Clear();
      InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => backend.PrintFileAsync(Job, temp.File("staged.document"), TimeSpan.FromSeconds(30), CancellationToken.None));
      Assert.Equal("The printer is not exposed to QQQ on this computer.", refused.Message);
      Assert.Equal(1, inner.Submissions);
   }



   /// <summary>
   /// Disposing a downloaded document releases its lease and deletes the staged file. If the file cannot be deleted,
   /// because something else still holds it or it was made read-only, disposal does not throw: a spool receipt already
   /// recorded must not be turned into a failure by cleanup.
   /// </summary>
   [Theory]
   [InlineData("free", false)]
   [InlineData("held-elsewhere", true)]
   [InlineData("read-only", true)]
   public void DisposingAStagedDocumentNeverThrows(string obstacle, bool remains)
   {
      using var temp = new TempDirectory();
      string path = temp.File("staged.document");
      File.WriteAllBytes(path, [1, 2, 3]);
      var document = new DownloadedDocument(path, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
      Assert.Equal(path, document.Path);
      using FileStream? holder = string.Equals(obstacle, "held-elsewhere", StringComparison.Ordinal) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
      if(string.Equals(obstacle, "read-only", StringComparison.Ordinal))
      { File.SetAttributes(path, FileAttributes.ReadOnly); }
      document.Dispose();
      Assert.Equal(remains, File.Exists(path));
      if(remains)
      { File.SetAttributes(path, FileAttributes.Normal); }
   }



   /// <summary>The settings parser accepts only an object whose members are exactly the known settings, each once and each an integer; anything else is refused whole rather than partly applied.</summary>
   [Theory]
   [InlineData("[]", "Realtime settings must be an object.")]
   [InlineData("7", "Realtime settings must be an object.")]
   [InlineData("extra", "Realtime settings contain an unknown, duplicate or non-integer field.")]
   [InlineData("repeated", "Realtime settings contain an unknown, duplicate or non-integer field.")]
   [InlineData("text", "Realtime settings contain an unknown, duplicate or non-integer field.")]
   [InlineData("fraction", "Realtime settings contain an unknown, duplicate or non-integer field.")]
   [InlineData("missing", "The server omitted a required realtime setting.")]
   [InlineData("out-of-range", "CONFIGURATION_REQUIRED: the realtime policy exceeds the advertised client limits or is inconsistent.")]
   public void SettingsThatAreNotExactlyThePolicyAreRefused(string fault, string message)
   {
      string valid = JsonSerializer.Serialize(new RealtimeSettings(Revision: 4), PrintApiClient.JsonOptions);
      string json = fault switch
      {
         "extra" => valid.Replace("{\"revision\":4", "{\"surprise\":1,\"revision\":4", StringComparison.Ordinal),
         "repeated" => valid.Replace("{\"revision\":4", "{\"revision\":3,\"revision\":4", StringComparison.Ordinal),
         "text" => valid.Replace("\"revision\":4", "\"revision\":\"4\"", StringComparison.Ordinal),
         "fraction" => valid.Replace("\"revision\":4", "\"revision\":4.5", StringComparison.Ordinal),
         "missing" => valid.Replace("{\"revision\":4,", "{", StringComparison.Ordinal),
         "out-of-range" => valid.Replace("\"maxCopies\":100", "\"maxCopies\":0", StringComparison.Ordinal),
         _ => fault,
      };
      using var document = JsonDocument.Parse(json);
      Assert.Equal(message, Assert.Throws<InvalidDataException>(() => RealtimeSettings.Parse(document.RootElement)).Message);
      using var accepted = JsonDocument.Parse(valid);
      Assert.Equal(new RealtimeSettings(Revision: 4), RealtimeSettings.Parse(accepted.RootElement));
   }



   /// <summary>The ceilings sent in HELLO are the largest values the policy validation accepts, so the server is never told the agent supports more than it will take; and only PDF and ZPL have a size limit to look up.</summary>
   [Fact]
   public void AdvertisedCeilingsMatchWhatValidationAccepts()
   {
      using var limits = JsonDocument.Parse(JsonSerializer.Serialize(RealtimeSettings.ClientLimits, PrintApiClient.JsonOptions));
      Assert.Equal(9, limits.RootElement.EnumerateObject().Count());
      var atCeiling = new RealtimeSettings(
         MaxPdfBytes: limits.RootElement.GetProperty("maxPdfBytes").GetInt64(), MaxZplBytes: limits.RootElement.GetProperty("maxZplBytes").GetInt64(),
         MaxFrameBytes: limits.RootElement.GetProperty("maxFrameBytes").GetInt32(), MaxQueuedFrames: limits.RootElement.GetProperty("maxQueuedFrames").GetInt32(),
         MaxQueuedFrameBytes: limits.RootElement.GetProperty("maxQueuedFrameBytes").GetInt64(), MaxConcurrentDownloads: limits.RootElement.GetProperty("maxConcurrentDownloads").GetInt32(),
         MaxConcurrentPrints: limits.RootElement.GetProperty("maxConcurrentPrints").GetInt32(), MaxStagedBytes: limits.RootElement.GetProperty("maxStagedBytes").GetInt64(),
         MaxCopies: limits.RootElement.GetProperty("maxCopies").GetInt32());
      atCeiling.Validate();
      Assert.Throws<InvalidDataException>(() => (atCeiling with { MaxCopies = atCeiling.MaxCopies + 1 }).Validate());
      Assert.Throws<InvalidDataException>(() => (atCeiling with { MaxFrameBytes = atCeiling.MaxFrameBytes + 1 }).Validate());
      Assert.Equal(atCeiling.MaxPdfBytes, atCeiling.MaximumDocumentBytes("PDF"));
      Assert.Equal(atCeiling.MaxZplBytes, atCeiling.MaximumDocumentBytes("ZPL"));
      Assert.Equal("Unsupported document format.", Assert.Throws<InvalidDataException>(() => atCeiling.MaximumDocumentBytes("PNG")).Message);
   }



   /// <summary>Explicit job paper is converted to whole microns, and no paper request means the queue default applies.</summary>
   [Fact]
   public void JobPaperIsConvertedToMicrons()
   {
      Assert.Equal((101_600, 152_400), new PrintOptions(PaperWidthMm: 101.6, PaperHeightMm: 152.4).CustomPaperMicrons());
      Assert.Null(new PrintOptions().CustomPaperMicrons());
   }



   /// <summary>New server limits apply to the next frame once they pass validation; limits that fail validation are refused and the working ones stay in force.</summary>
   [Fact]
   public async Task ChannelAppliesOnlyValidatedLimits()
   {
      using var socket = new ScriptedWebSocket();
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      var payload = new { text = new string('x', 9_000) };
      await channel.SendAsync("HEARTBEAT", payload);
      Assert.Throws<InvalidDataException>(() => channel.UpdateSettings(new RealtimeSettings(MaxFrameBytes: 1)));
      await channel.SendAsync("HEARTBEAT", payload);
      channel.UpdateSettings(new RealtimeSettings(MaxFrameBytes: 8_192, MaxQueuedFrameBytes: 8_192));
      Assert.Equal("Realtime frame exceeds the negotiated limit.", (await Assert.ThrowsAsync<InvalidDataException>(() => channel.SendAsync("HEARTBEAT", payload))).Message);
   }



   /// <summary>
   /// The channel refuses rather than drops: the same durable message cannot be in flight twice, a full outgoing queue
   /// aborts the socket so the runtime reconnects and replays, a cancelled caller sends nothing, and disposing the channel
   /// fails every send still waiting for its acknowledgement.
   /// </summary>
   [Fact]
   public async Task ChannelRefusesInsteadOfDroppingFrames()
   {
      using var socket = new ScriptedWebSocket();
      var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings(MaxQueuedFrames: 2));
      using var cancelled = new CancellationTokenSource();
      await cancelled.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.SendAsync("HEARTBEAT", new { }, token: cancelled.Token));

      Task waiting = channel.SendAsync("JOB_EVENT", new { jobId = "job-1" }, "event-1", acknowledged: true);
      Assert.Equal("The same durable message is already awaiting acknowledgement.",
         (await Assert.ThrowsAsync<InvalidOperationException>(() => channel.SendAsync("JOB_EVENT", new { jobId = "job-1" }, "event-1", acknowledged: true))).Message);
      await channel.SendAsync("HEARTBEAT", new { });
      Assert.Equal(0, socket.Aborts);
      Assert.Equal("Realtime output queue is full; reconnect is required.", (await Assert.ThrowsAsync<IOException>(() => channel.SendAsync("HEARTBEAT", new { }))).Message);
      Assert.Equal(1, socket.Aborts);

      Assert.False(waiting.IsCompleted);
      channel.Dispose();
      Assert.Equal("Realtime connection was disposed.", (await Assert.ThrowsAsync<RealtimeChannelClosedException>(() => waiting)).Message);
      Assert.True(socket.Disposed);
      Assert.False(channel.IsOpen);
   }



   /// <summary>The socket handed to the runtime passes every call straight to the connected socket and, when disposed, also disposes the HTTP transport that carried its upgrade, which must outlive the socket.</summary>
   [Fact]
   public async Task OwnedSocketDelegatesAndDisposesItsTransportLast()
   {
      using var inner = new ScriptedWebSocket();
      using var handler = new DisposalRecordingHandler();
      using var invoker = new HttpMessageInvoker(handler);
      var socket = new OwnedPrintingSocket(inner, invoker);
      Assert.Equal(WebSocketState.Open, socket.State);
      Assert.Equal("qqq-printing.v1", socket.SubProtocol);
      Assert.Null(socket.CloseStatus);

      await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"HELLO"}"""), WebSocketMessageType.Text, true, CancellationToken.None);
      Assert.Equal("HELLO", (await inner.NextSentAsync()).GetProperty("type").GetString());
      inner.Deliver("""{"type":"WELCOME"}""");
      byte[] buffer = new byte[64];
      WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, CancellationToken.None);
      Assert.Equal("""{"type":"WELCOME"}""", Encoding.UTF8.GetString(buffer, 0, received.Count));

      await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done sending", CancellationToken.None);
      await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "goodbye", CancellationToken.None);
      Assert.Equal(new[] { "close-output NormalClosure done sending", "close NormalClosure goodbye" }, inner.Closes, StringComparer.Ordinal);
      Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
      Assert.Equal("goodbye", socket.CloseStatusDescription);
      socket.Abort();
      Assert.Equal(1, inner.Aborts);
      Assert.False(handler.Disposed);
      socket.Dispose();
      Assert.True(inner.Disposed);
      Assert.True(handler.Disposed);
   }



   /// <summary>
   /// The counting stream reports exactly the bytes that moved in its own direction (reads for a download, writes for an
   /// upload), through every read and write overload, including a read that returns nothing at the end; everything
   /// else is passed to the wrapped stream untouched.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task CountingStreamCountsOnlyItsOwnDirection(bool countReads)
   {
      long counted = 0;
      using var inner = new MemoryStream();
      inner.Write([1, 2, 3, 4, 5, 6, 7, 8]);
      inner.Position = 0;
      using var stream = new TrafficStream(inner, bytes => counted += bytes, countReads);
      Assert.True(stream.CanRead);
      Assert.True(stream.CanSeek);
      Assert.True(stream.CanWrite);
      Assert.Equal(8, stream.Length);

      byte[] buffer = new byte[2];
      Assert.Equal(2, stream.Read(buffer, 0, 2));
      Assert.Equal(2, stream.Read(buffer.AsSpan()));
      // CA1835: the array overloads are deliberately called here; they are part of what is being tested.
#pragma warning disable CA1835
      Assert.Equal(2, await stream.ReadAsync(buffer, 0, 2, CancellationToken.None));
#pragma warning restore CA1835
      Assert.Equal(2, await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None));
      Assert.Equal(0, await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None));
      Assert.Equal(countReads ? 8 : 0, counted);

      counted = 0;
      stream.Write([1, 2], 0, 2);
      stream.Write(new byte[] { 3 }.AsSpan());
#pragma warning disable CA1835
      await stream.WriteAsync(new byte[] { 4, 5 }, 0, 2, CancellationToken.None);
#pragma warning restore CA1835
      await stream.WriteAsync(new byte[] { 6 }.AsMemory(), CancellationToken.None);
      stream.Flush();
      await stream.FlushAsync(CancellationToken.None);
      Assert.Equal(countReads ? 0 : 6, counted);
      Assert.Equal(14, inner.Length);

      stream.Position = 1;
      Assert.Equal(1, stream.Position);
      Assert.Equal(0, stream.Seek(0, SeekOrigin.Begin));
      stream.SetLength(4);
      Assert.Equal(4, inner.Length);
   }



   /// <summary>The counting stream owns the wrapped stream unless told to leave it open, so wrapping a network stream to count an upload does not close the connection.</summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public void CountingStreamDisposesTheWrappedStreamUnlessToldToLeaveItOpen(bool leaveOpen)
   {
      using var inner = new MemoryStream();
      new TrafficStream(inner, _ => { }, countReads: false, leaveOpen).Dispose();
      Assert.Equal(leaveOpen, inner.CanWrite);
   }



   /// <summary>A caller without a cancellation token reaches the same counted upload path as the platform's own cancellable call: the body arrives intact and each byte is counted once.</summary>
   [Fact]
   public async Task CountingContentCountsAnUploadFromTheTokenlessEntryPoint()
   {
      long counted = 0;
      using var content = new TrafficHttpContent(new StringContent("synthetic body", Encoding.UTF8, "text/plain"), bytes => counted += bytes);
      using var destination = new MemoryStream();
      MethodInfo serialize = typeof(HttpContent).GetMethod("SerializeToStreamAsync", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(Stream), typeof(TransportContext)])!;
      await (Task)serialize.Invoke(content, [destination, null])!;
      Assert.Equal("synthetic body", Encoding.UTF8.GetString(destination.ToArray()));
      Assert.Equal(14, counted);
      Assert.Equal("text/plain", content.Headers.ContentType!.MediaType);
      Assert.True(destination.CanWrite);
   }



   /// <summary>An HTTP handler that only records whether it was disposed.</summary>
   private sealed class DisposalRecordingHandler : HttpMessageHandler
   {
      /// <summary>True once the handler was disposed.</summary>
      internal bool Disposed { get; private set; }



      /// <inheritdoc/>
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new NotSupportedException();



      /// <summary>Records the disposal.</summary>
      protected override void Dispose(bool disposing)
      {
         Disposed = true;
         base.Dispose(disposing);
      }
   }
}
