/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.Http.Json;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// The agent's client for the QQQ printing API. It is built from a signed-in session and sends every request through
/// it. The base part holds the HTTP calls (enrollment, claim, queued jobs and operator actions) and sends inventory and
/// job events over the realtime channel, which is the only way they travel. Other parts add the session, downloads,
/// the channel itself and control calls.
/// </summary>
public sealed partial class PrintApiClient : IDisposable
{
   public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
   private readonly HttpClient _http;
   private readonly TimeProvider _time;



   /// <summary>The agent's assembly version, sent when enrolling, in the realtime hello and with heartbeats, so the server can show which build each device runs.</summary>
   public static string AgentVersion => typeof(PrintApiClient).Assembly.GetName().Version?.ToString() ?? "unknown";



   /// <summary>
   /// Creates a client whose every request goes through the given signed-in session, to the printing extension's path on
   /// the session's server. There is no other way to build one, so the client cannot send anything outside a session,
   /// and it holds no credential of its own: the session adds what authenticates a request. The server address is the
   /// session's, which the session client has already required to be HTTPS or explicitly allowed loopback HTTP. Responses
   /// are limited to 2 MiB and requests to 60 seconds. The download limits are counted on the supplied clock, the system
   /// clock by default. The 60 second request limit is not: it is <c>HttpClient.Timeout</c>, which the platform enforces
   /// on the system clock whatever clock is supplied.
   /// </summary>
   private PrintApiClient(IQqqSessionClient session, TimeProvider? timeProvider)
   {
      _session = session;
      _time = timeProvider ?? TimeProvider.System;
      _http = new HttpClient(new PrintingSessionHandler(session))
      {
         BaseAddress = new Uri(session.Origin, PrintingSessionHandler.Prefix),
         Timeout = TimeSpan.FromSeconds(60),
         MaxResponseContentBufferSize = 2 * 1024 * 1024,
      };
   }



   /// <summary>Enrolls this computer with a short-lived code, on the enrollment route of the signed-in session, and returns the device identity and its secondary proof. A protocol version other than 1 is rejected rather than guessed at.</summary>
   public async Task<EnrollmentResponse> EnrollAsync(EnrollmentRequest request, CancellationToken token = default)
   {
      using HttpResponseMessage response = await _http.PostAsJsonAsync("devices/enroll", request, JsonOptions, token).ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
      EnrollmentResponse result = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(token).ConfigureAwait(false)
         ?? throw new InvalidDataException("Missing enrollment response.");
      if(result.ProtocolVersion != 1)
      {
         throw new InvalidDataException("Unsupported printing protocol.");
      }

      return result;
   }



   /// <summary>Reports the printers this computer shares, over the realtime channel, and waits for the server's acknowledgement.</summary>
   public async Task InventoryAsync(IReadOnlyList<PrinterInfo> printers, CancellationToken token)
   {
      await RealtimeChannel.SendAsync("INVENTORY", new { printers }, acknowledged: true, token: token).ConfigureAwait(false);
   }



   /// <summary>
   /// Asks the server for the next job for this device, or null when none is waiting. The server replays a claimed job
   /// until its RECEIVED event commits, so the caller must record it. The job is checked against the contract before it
   /// is returned: one that breaks it throws <see cref="PrintingProtocolException"/>, carrying a stand-in when the job
   /// named itself, so nothing later ever handles a job with a missing member or an unsafe identifier. Fields the agent
   /// does not read are ignored, and a byte-order mark in front of the body is accepted.
   /// </summary>
   public async Task<PrintJob?> ClaimAsync(CancellationToken token)
   {
      using HttpResponseMessage response = await _http.PostAsJsonAsync("device/jobs/claim", new { }, JsonOptions, token).ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
      return PrintJobContract.ReadClaim(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
   }



   /// <summary>
   /// Delivers one job event over the realtime channel. The message id is derived from the event id and the server must
   /// acknowledge it, so a replay after a lost acknowledgement carries the same identity and is not applied twice.
   /// </summary>
   public async Task EventAsync(string jobId, PrintEvent printEvent, CancellationToken token)
   {
      await RealtimeChannel.SendAsync("JOB_EVENT", new { jobId, @event = printEvent }, "event-" + printEvent.EventId, acknowledged: true, token: token).ConfigureAwait(false);
   }



   /// <summary>Releases the HTTP client.</summary>
   public void Dispose() => _http.Dispose();



   /// <summary>
   /// Lists jobs the server is holding for this device but this computer has not received. A server without the endpoint
   /// (404) is treated as having none, so an older server only loses the preview. Each entry must name its job and
   /// printer once, with usable identifiers; a list that breaks the contract throws
   /// <see cref="PrintingProtocolException"/>. Fields the agent does not read are ignored, and a byte-order mark in
   /// front of the body is accepted.
   /// </summary>
   public async Task<IReadOnlyList<PrintJob>> QueuedJobsAsync(CancellationToken token)
   {
      using HttpResponseMessage response = await _http.GetAsync("device/jobs/queued", token).ConfigureAwait(false);
      if(response.StatusCode == System.Net.HttpStatusCode.NotFound)
      { return []; }
      response.EnsureSuccessStatusCode();
      return PrintJobContract.ReadQueued(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
   }



   /// <summary>Request an explicit operator action without retaining document bytes locally.</summary>
   public Task<string> OperatorActionAsync(string operation, string? jobId, string? printerId, string? format, bool confirmDuplicateRisk, CancellationToken token)
      => OperatorActionAsync(operation, jobId, printerId, format, confirmDuplicateRisk, null, token);



   /// <summary>
   /// Asks the server to run a test print, retry or cancel as an explicit operator action. Each call carries a fresh request
   /// identifier, the retry flag states that the operator accepted the duplicate-print risk, and a refusal is turned into the
   /// server's own message when it gave one. Returns the new job identifier, or empty when the action created none.
   /// </summary>
   public async Task<string> OperatorActionAsync(string operation, string? jobId, string? printerId, string? format, bool confirmDuplicateRisk, string? testProfile, CancellationToken token)
   {
      string path = operation switch
      {
         "test-print" => "device/test-print",
         "retry-job" => $"device/jobs/{Uri.EscapeDataString(jobId ?? throw new ArgumentException("Job is required.", nameof(jobId)))}/retry",
         "cancel-job" => $"device/jobs/{Uri.EscapeDataString(jobId ?? throw new ArgumentException("Job is required.", nameof(jobId)))}/cancel",
         _ => throw new ArgumentException("Unsupported operator action.", nameof(operation)),
      };
      using HttpResponseMessage response = await _http.PostAsJsonAsync(path, new
      {
         printerId,
         format,
         confirmDuplicateRisk,
         testProfile = string.Equals(operation, "test-print", StringComparison.Ordinal) ? testProfile : null,
         requestId = Guid.NewGuid().ToString("N"),
      }, JsonOptions, token).ConfigureAwait(false);
      if(!response.IsSuccessStatusCode)
      {
         if(response.StatusCode == System.Net.HttpStatusCode.NotFound)
         {
            throw new InvalidOperationException("The job was not found or this QQQ server needs the printing operator-actions update.");
         }
         string message = "QQQ did not accept the action. Check printer approval, connection, and job state.";
         using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
         if(error.RootElement.TryGetProperty("message", out JsonElement value))
         { message = value.GetString() ?? message; }
         throw new InvalidOperationException(message);
      }
      using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
      return result.RootElement.TryGetProperty("jobId", out JsonElement id) ? id.GetString() ?? "" : "";
   }
}
