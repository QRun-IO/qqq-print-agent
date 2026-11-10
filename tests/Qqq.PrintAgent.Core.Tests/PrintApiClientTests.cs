/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// The API client's remaining calls: enrollment, the routes that move onto the realtime channel when one is attached,
/// the HTTP control path, operator actions, and what a download refuses. Each is driven against a scripted server or
/// socket and asserts what was sent as well as what came back.
/// </summary>
public sealed class PrintApiClientTests
{
   private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);



   /// <summary>
   /// Enrollment posts the code, device name and agent version to the route of its transport (the device route with a
   /// token, the native route through a session) and returns the identity from the shared fixture. The request and the
   /// response, which carry the enrollment code and the new device token, print as redacted labels.
   /// </summary>
   [Theory]
   [InlineData(false, "/printing/v1/enroll")]
   [InlineData(true, "devices/enroll")]
   public async Task EnrollmentPostsToTheRouteOfItsTransport(bool throughSession, string route)
   {
      string fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "enroll.response.json"));
      using var server = new ScriptedServer();
      using var session = new FakeSessionClient();
      server.Add(fixture);
      session.Answer(fixture);
      using PrintApiClient api = throughSession ? PrintApiClient.ForSession(session) : new PrintApiClient("https://synthetic.invalid", handler: server);
      var request = new EnrollmentRequest("SYNTHETIC-CODE", "Front desk", "2.0.0");
      EnrollmentResponse response = await api.EnrollAsync(request);
      Assert.Equal(1, response.ProtocolVersion);
      Assert.NotEmpty(response.DeviceId);
      Assert.NotEmpty(response.DeviceToken);
      string body = throughSession ? Assert.Single(session.Printing).Body! : Assert.Single(server.Requests).Body!;
      Assert.Equal(route, throughSession ? session.Printing[0].Path : server.Requests[0].Uri.AbsolutePath);
      using var sent = JsonDocument.Parse(body);
      Assert.Equal("SYNTHETIC-CODE", sent.RootElement.GetProperty("enrollmentCode").GetString());
      Assert.Equal("Front desk", sent.RootElement.GetProperty("deviceName").GetString());
      Assert.Equal("2.0.0", sent.RootElement.GetProperty("agentVersion").GetString());
      Assert.Equal("Enrollment request (redacted)", request.ToString());
      Assert.Equal("Enrollment response (redacted)", response.ToString());
      Assert.Equal("Agent configuration (redacted)", new AgentConfiguration("https://synthetic.invalid", response.DeviceId, response.DeviceToken).ToString());
      Assert.Equal(5, new AgentConfiguration("https://synthetic.invalid", "device", "token").PollIntervalSeconds);
   }



   /// <summary>An enrollment answer for another protocol version, an empty answer, or a refusal is never turned into a device identity.</summary>
   [Theory]
   [InlineData("""{"protocolVersion":2,"deviceId":"device-1","deviceToken":"synthetic-token","pollIntervalSeconds":5}""", HttpStatusCode.OK, typeof(InvalidDataException), "Unsupported printing protocol.")]
   [InlineData("null", HttpStatusCode.OK, typeof(InvalidDataException), "Missing enrollment response.")]
   [InlineData("""{"code":"ENROLLMENT_EXPIRED"}""", HttpStatusCode.Gone, typeof(HttpRequestException), null)]
   public async Task EnrollmentRefusesAnAnswerItCannotUse(string body, HttpStatusCode status, Type failure, string? message)
   {
      using var server = new ScriptedServer();
      server.Add(body, status);
      using var api = new PrintApiClient("https://synthetic.invalid", handler: server);
      Exception thrown = await Assert.ThrowsAsync(failure, () => api.EnrollAsync(new EnrollmentRequest("SYNTHETIC-CODE", "Front desk", "2.0.0")));
      if(message is not null)
      { Assert.Equal(message, thrown.Message); }
   }



   /// <summary>
   /// With a realtime channel attached, inventory, heartbeat, job events and command receipts travel as frames instead of
   /// HTTP requests. Inventory, events and receipts wait for the server's ACK and carry a message identity derived from
   /// the durable record, so a replay is recognised; a heartbeat does not wait.
   /// </summary>
   [Fact]
   public async Task AttachedChannelCarriesInventoryHeartbeatEventsAndReceipts()
   {
      using var socket = new ScriptedWebSocket();
      using var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings());
      using var stop = new CancellationTokenSource();
      Task run = channel.RunAsync(_ => { }, stop.Token);
      using var server = new ScriptedServer();
      using PrintApiClient api = new PrintApiClient("https://synthetic.invalid", handler: server).WithRealtime(channel, new RealtimeSettings());
      Assert.True(api.UsesRealtime);

      Task inventory = api.InventoryAsync([new PrinterInfo("printer1", "Queue one", "Synthetic driver", "USB001", "READY", ["ZPL"])], CancellationToken.None);
      JsonElement frame = await socket.NextSentAsync();
      Assert.Equal("INVENTORY", frame.GetProperty("type").GetString());
      Assert.Equal("printer1", frame.GetProperty("payload").GetProperty("printers")[0].GetProperty("printerId").GetString());
      Assert.False(inventory.IsCompleted);
      socket.Deliver("ACK", "ack-1", new { acknowledgedMessageId = frame.GetProperty("messageId").GetString() });
      await inventory.WaitAsync(Patience);

      await api.HeartbeatAsync(new DeviceProfile(Name: "Front desk"), CancellationToken.None).WaitAsync(Patience);
      frame = await socket.NextSentAsync();
      Assert.Equal("HEARTBEAT", frame.GetProperty("type").GetString());
      Assert.Equal(PrintApiClient.AgentVersion, frame.GetProperty("payload").GetProperty("agentVersion").GetString());
      Assert.Equal("Front desk", frame.GetProperty("payload").GetProperty("profile").GetProperty("name").GetString());

      Task jobEvent = api.EventAsync("job-1", new PrintEvent("e1", "SPOOLED", SpoolerJobId: "42"), CancellationToken.None);
      frame = await socket.NextSentAsync();
      Assert.Equal("JOB_EVENT", frame.GetProperty("type").GetString());
      Assert.Equal("event-e1", frame.GetProperty("messageId").GetString());
      Assert.Equal("job-1", frame.GetProperty("payload").GetProperty("jobId").GetString());
      Assert.Equal("SPOOLED", frame.GetProperty("payload").GetProperty("event").GetProperty("status").GetString());
      socket.Deliver("ACK", "ack-2", new { acknowledgedMessageId = "event-e1" });
      await jobEvent.WaitAsync(Patience);

      Task receipt = api.ControlReceiptAsync("command-1", new DeviceControlReceipt("r1", "COMPLETED", "2026-01-01T00:00:00Z"), CancellationToken.None);
      frame = await socket.NextSentAsync();
      Assert.Equal("COMMAND_RECEIPT", frame.GetProperty("type").GetString());
      Assert.Equal("receipt-r1", frame.GetProperty("messageId").GetString());
      Assert.Equal("command-1", frame.GetProperty("payload").GetProperty("commandId").GetString());
      Assert.Equal("COMPLETED", frame.GetProperty("payload").GetProperty("receipt").GetProperty("outcome").GetString());
      socket.Deliver("ACK", "ack-3", new { acknowledgedMessageId = "receipt-r1" });
      await receipt.WaitAsync(Patience);

      Assert.Empty(server.Requests);
      await stop.CancelAsync();
      await run.WaitAsync(TimeSpan.FromSeconds(10));
   }



   /// <summary>When the runtime supplies a provider for the current channel, sends go to the channel it returns at that moment, so a reconnect is picked up without rebuilding the API client.</summary>
   [Fact]
   public async Task ProviderSuppliesTheCurrentChannelAfterAReconnect()
   {
      using var staleSocket = new ScriptedWebSocket();
      using var currentSocket = new ScriptedWebSocket();
      using var stale = new PrintingRealtimeChannel(staleSocket, new RealtimeSettings());
      using var current = new PrintingRealtimeChannel(currentSocket, new RealtimeSettings());
      using var stop = new CancellationTokenSource();
      Task run = current.RunAsync(_ => { }, stop.Token);
      using PrintApiClient api = new PrintApiClient("https://synthetic.invalid").WithRealtime(stale, new RealtimeSettings(), () => current);
      await api.HeartbeatAsync(CancellationToken.None).WaitAsync(Patience);
      Assert.Equal("HEARTBEAT", (await currentSocket.NextSentAsync()).GetProperty("type").GetString());
      await stop.CancelAsync();
      await run.WaitAsync(TimeSpan.FromSeconds(10));
   }



   /// <summary>
   /// An operator action goes to its own route with a fresh request identifier; only a test print carries a test profile,
   /// and a retry states that the operator accepted the duplicate risk. The new job identifier is returned when the server
   /// gives one.
   /// </summary>
   [Theory]
   [InlineData("test-print", "/printing/v1/device/test-print", """{"jobId":"job-9"}""", "job-9")]
   [InlineData("retry-job", "/printing/v1/device/jobs/job%2F1/retry", "{}", "")]
   [InlineData("cancel-job", "/printing/v1/device/jobs/job%2F1/cancel", """{"jobId":null}""", "")]
   public async Task OperatorActionGoesToItsRouteAndReturnsTheNewJob(string operation, string route, string body, string expected)
   {
      using var server = new ScriptedServer();
      server.Add(body);
      using var api = new PrintApiClient("https://synthetic.invalid", handler: server);
      Assert.Equal(expected, await api.OperatorActionAsync(operation, "job/1", "printer1", "ZPL", confirmDuplicateRisk: true, "LABEL_4X6_203", CancellationToken.None));
      ScriptedRequest request = Assert.Single(server.Requests);
      Assert.Equal(route, request.Uri.AbsolutePath);
      using var sent = JsonDocument.Parse(request.Body!);
      Assert.Equal("printer1", sent.RootElement.GetProperty("printerId").GetString());
      Assert.True(sent.RootElement.GetProperty("confirmDuplicateRisk").GetBoolean());
      Assert.Equal(32, sent.RootElement.GetProperty("requestId").GetString()!.Length);
      Assert.Equal(string.Equals(operation, "test-print", StringComparison.Ordinal), sent.RootElement.TryGetProperty("testProfile", out _));
   }



   /// <summary>
   /// A refused operator action surfaces the server's own explanation when it gave one and a general one otherwise; a 404
   /// says the job or the server feature is missing; and an unknown action or a job action without a job is rejected
   /// before any request.
   /// </summary>
   [Theory]
   [InlineData("retry-job", "job-1", HttpStatusCode.Conflict, """{"message":"The job is already printing."}""", typeof(InvalidOperationException), "The job is already printing.")]
   [InlineData("retry-job", "job-1", HttpStatusCode.Conflict, "{}", typeof(InvalidOperationException), "QQQ did not accept the action. Check printer approval, connection, and job state.")]
   [InlineData("retry-job", "job-1", HttpStatusCode.Conflict, """{"message":null}""", typeof(InvalidOperationException), "QQQ did not accept the action. Check printer approval, connection, and job state.")]
   [InlineData("cancel-job", "job-1", HttpStatusCode.NotFound, "{}", typeof(InvalidOperationException), "The job was not found or this QQQ server needs the printing operator-actions update.")]
   [InlineData("delete-job", "job-1", HttpStatusCode.OK, "{}", typeof(ArgumentException), null)]
   [InlineData("retry-job", null, HttpStatusCode.OK, "{}", typeof(ArgumentException), null)]
   [InlineData("cancel-job", null, HttpStatusCode.OK, "{}", typeof(ArgumentException), null)]
   public async Task RefusedOperatorActionExplainsItself(string operation, string? jobId, HttpStatusCode status, string body, Type failure, string? message)
   {
      using var server = new ScriptedServer();
      server.Add(body, status);
      using var api = new PrintApiClient("https://synthetic.invalid", handler: server);
      Exception thrown = await Assert.ThrowsAsync(failure, () => api.OperatorActionAsync(operation, jobId, "printer1", "ZPL", confirmDuplicateRisk: false, CancellationToken.None));
      if(message is not null)
      { Assert.Equal(message, thrown.Message); }
      Assert.Equal(failure == typeof(ArgumentException) ? 0 : 1, server.Requests.Count);
   }



   /// <summary>
   /// A staged download is refused before any request when the manifest declares nothing or more than the format allows;
   /// refused when the response announces a different length; and refused the moment the body runs past the declared
   /// length. No file is left behind in any of these cases.
   /// </summary>
   [Theory]
   [InlineData("empty", "Document exceeds the negotiated format or staging limit.", 0)]
   [InlineData("over-format", "Document exceeds the negotiated format or staging limit.", 0)]
   [InlineData("announced-length", "Document length does not match its manifest.", 1)]
   [InlineData("overrun", "Document exceeds its declared length.", 1)]
   public async Task StagedDownloadRefusesWhatDoesNotMatchItsManifest(string fault, string message, int requests)
   {
      byte[] document = RandomNumberGenerator.GetBytes(64);
      PrintJob job = FakePrintServer.Job(document, format: "PDF");
      RealtimeSettings settings = new(MaxPdfBytes: 1_000, MaxZplBytes: 1_000, MaxStagedBytes: 2_000);
      job = fault switch
      {
         "empty" => job with { ContentLength = 0 },
         "over-format" => job with { ContentLength = 1_001 },
         "announced-length" => job with { ContentLength = 63 },
         _ => job with { ContentLength = 32 },
      };
      using var staging = new TempDirectory();
      using var body = new FeedStream();
      using var server = new ScriptedServer();
      server.AddResponse(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = string.Equals(fault, "overrun", StringComparison.Ordinal) ? new StreamContent(body) : new ByteArrayContent(document) });
      body.Feed(document);
      using var api = new PrintApiClient("https://synthetic.invalid", handler: server);
      InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(() => api.DownloadFileAsync(job, staging.Path, settings, CancellationToken.None));
      Assert.Equal(message, refused.Message);
      Assert.Equal(requests, server.Requests.Count);
      Assert.Empty(Directory.EnumerateFileSystemEntries(staging.Path));
   }



   /// <summary>The in-memory download refuses an empty or over-20 MiB manifest before any request and stops as soon as the body runs past the declared length.</summary>
   [Theory]
   [InlineData(0L, "Document exceeds the 20 MiB limit or is empty.", 0)]
   [InlineData(20_971_521L, "Document exceeds the 20 MiB limit or is empty.", 0)]
   [InlineData(8L, "Document length mismatch.", 1)]
   public async Task InMemoryDownloadRefusesWhatDoesNotMatchItsManifest(long declared, string message, int requests)
   {
      byte[] document = new byte[16];
      using var server = new ScriptedServer();
      server.AddResponse(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(document) });
      using var api = new PrintApiClient("https://synthetic.invalid", handler: server);
      InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(() => api.DownloadAsync(FakePrintServer.Job(document) with { ContentLength = declared }, CancellationToken.None));
      Assert.Equal(message, refused.Message);
      Assert.Equal(requests, server.Requests.Count);
   }



   /// <summary>
   /// Requests made through a session are confined to the printing extension: an address under it is handed to the
   /// session as a relative route with its query, and any other address is refused before the session sees it.
   /// </summary>
   [Fact]
   public async Task SessionHandlerConfinesRequestsToThePrintingExtension()
   {
      using var session = new FakeSessionClient();
      session.Answer("{}");
      using var invoker = new HttpMessageInvoker(new PrintingSessionHandler(session));
      using var inside = new HttpRequestMessage(HttpMethod.Get, "https://printing.example.invalid/qqq/v1/qbit/printing/operator/printers?limit=200&after=a%2Fb");
      using var outside = new HttpRequestMessage(HttpMethod.Get, "https://printing.example.invalid/qqq/v1/manageSession");
      using HttpResponseMessage response = await invoker.SendAsync(inside, CancellationToken.None);
      (HttpMethod method, string path, string? body) = Assert.Single(session.Printing);
      Assert.Equal(HttpMethod.Get, method);
      Assert.Equal("operator/printers?limit=200&after=a%2Fb", path);
      Assert.Null(body);
      await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.SendAsync(outside, CancellationToken.None));
      Assert.Single(session.Printing);
   }



   /// <summary>A session transport that does not implement provider sign-out or the realtime socket falls back to the interface's defaults: an ordinary sign-out with nothing to open, and a loud refusal to pretend realtime printing works.</summary>
   [Fact]
   public async Task SessionInterfaceDefaultsSignOutPlainlyAndRefuseRealtime()
   {
      using var fake = new FakeSessionClient();
      IQqqSessionClient session = fake;
      Assert.Null(await session.SignOutWithProviderAsync(new Uri(ScriptedServer.Callback)));
      Assert.Equal(1, fake.SignOuts);
      NotSupportedException refused = await Assert.ThrowsAsync<NotSupportedException>(() => session.ConnectPrintingAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60)));
      Assert.Equal("This session transport does not support realtime printing.", refused.Message);
   }
}
