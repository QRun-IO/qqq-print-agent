/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// The agent's client for the QQQ printing API. The base part holds the HTTP calls that remain (enrollment, claim, content,
/// queued jobs and operator actions) and routes inventory, heartbeat and events over the realtime channel whenever one is
/// attached. Other parts add the native session, downloads, realtime and control calls.
/// </summary>
public sealed partial class PrintApiClient : IDisposable
{
   public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
   private readonly HttpClient _http;



   /// <summary>The agent's assembly version, sent with heartbeats so the server can show which build each device runs.</summary>
   public static string AgentVersion => typeof(PrintApiClient).Assembly.GetName().Version?.ToString() ?? "unknown";



   /// <summary>
   /// Creates a client for one server. The address must be HTTPS, or HTTP only on loopback for local development. Responses
   /// are limited to 2 MiB and requests to 60 seconds, and a bearer token is attached only when one is supplied (the legacy
   /// diagnostic path); native sessions authenticate through the session client instead.
   /// </summary>
   public PrintApiClient(string serverUrl, string? token = null, HttpMessageHandler? handler = null)
   {
      Uri uri = new(serverUrl.TrimEnd('/') + "/printing/v1/");
      if(!string.Equals(uri.Scheme, "https", StringComparison.Ordinal) && !(string.Equals(uri.Scheme, "http", StringComparison.Ordinal) && uri.IsLoopback))
      {
         throw new ArgumentException("Use HTTPS, or HTTP on loopback for local development.", nameof(serverUrl));
      }
      _http = handler is null ? new HttpClient() : new HttpClient(handler);
      _http.BaseAddress = uri;
      _http.Timeout = TimeSpan.FromSeconds(60);
      _http.MaxResponseContentBufferSize = 2 * 1024 * 1024;
      if(token is not null)
      {
         _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
      }
   }



   /// <summary>Enrolls this computer with a short-lived code and returns the device identity and its secondary proof. A protocol version other than 1 is rejected rather than guessed at.</summary>
   public async Task<EnrollmentResponse> EnrollAsync(EnrollmentRequest request, CancellationToken token = default)
   {
      using HttpResponseMessage response = await _http.PostAsJsonAsync(_nativeSession ? "devices/enroll" : "enroll", request, JsonOptions, token).ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
      EnrollmentResponse result = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(token).ConfigureAwait(false)
         ?? throw new InvalidDataException("Missing enrollment response.");
      if(result.ProtocolVersion != 1)
      {
         throw new InvalidDataException("Unsupported printing protocol.");
      }

      return result;
   }



   /// <summary>Reports the printers this computer shares, over the realtime channel when attached (waiting for the server's acknowledgement) and otherwise over HTTP.</summary>
   public async Task InventoryAsync(IReadOnlyList<PrinterInfo> printers, CancellationToken token)
   {
      if(_realtime is not null)
      {
         await RealtimeChannel.SendAsync("INVENTORY", new { printers }, acknowledged: true, token: token).ConfigureAwait(false);
         return;
      }
      using HttpResponseMessage response = await _http.PutAsJsonAsync("device/printers", new { printers }, JsonOptions, token).ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
   }



   /// <summary>Sends a liveness heartbeat without a device profile.</summary>
   public Task HeartbeatAsync(CancellationToken token) => HeartbeatAsync(null, token);



   /// <summary>Sends a liveness heartbeat carrying the agent version and, when given, the computer profile, over the realtime channel when attached and otherwise over HTTP.</summary>
   public async Task HeartbeatAsync(DeviceProfile? profile, CancellationToken token)
   {
      if(_realtime is not null)
      {
         await RealtimeChannel.SendAsync("HEARTBEAT", new { agentVersion = AgentVersion, profile }, token: token).ConfigureAwait(false);
         return;
      }
      using HttpResponseMessage response = await _http.PostAsJsonAsync("device/heartbeat", new { agentVersion = AgentVersion, profile }, JsonOptions, token).ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
   }



   /// <summary>Asks the server for the next job for this device, or null when none is waiting. The server replays a claimed job until its RECEIVED event commits, so the caller must record it.</summary>
   public async Task<PrintJob?> ClaimAsync(CancellationToken token)
   {
      using HttpResponseMessage response = await _http.PostAsJsonAsync("device/jobs/claim", new { }, JsonOptions, token).ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
      return (await response.Content.ReadFromJsonAsync<JobClaim>(token).ConfigureAwait(false))?.Job;
   }



   /// <summary>
   /// Downloads a whole document into memory, rejecting an empty or over-20 MiB declaration up front. Reading stops the moment
   /// the data exceeds the declared length, and the result must match the declared length and SHA-256 or it is refused, so a
   /// truncated or altered document can never be printed.
   /// </summary>
   public async Task<byte[]> DownloadAsync(PrintJob job, CancellationToken token)
   {
      if(job.ContentLength is < 1 or > 20_971_520)
      {
         throw new InvalidDataException("Document exceeds the 20 MiB limit or is empty.");
      }

      using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
      downloadTimeout.CancelAfter(_http.Timeout);
      token = downloadTimeout.Token;
      using HttpResponseMessage response = await _http.GetAsync($"device/jobs/{Uri.EscapeDataString(job.JobId)}/content", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
      await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
      using MemoryStream output = new();
      byte[] buffer = new byte[81920];
      int read;
      while((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
      {
         if(output.Length + read > job.ContentLength)
         {
            throw new InvalidDataException("Document length mismatch.");
         }

         await output.WriteAsync(buffer.AsMemory(0, read), token);
      }
      byte[] content = output.ToArray();
      if(content.LongLength != job.ContentLength || !string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)), job.ContentSha256, StringComparison.OrdinalIgnoreCase))
      {
         throw new InvalidDataException("Document integrity check failed.");
      }

      return content;
   }



   /// <summary>
   /// Delivers one job event. Over the realtime channel the message id is derived from the event id and the server must
   /// acknowledge it, so a replay after a lost acknowledgement carries the same identity and is not applied twice.
   /// </summary>
   public async Task EventAsync(string jobId, PrintEvent printEvent, CancellationToken token)
   {
      if(_realtime is not null)
      {
         await RealtimeChannel.SendAsync("JOB_EVENT", new { jobId, @event = printEvent }, "event-" + printEvent.EventId, acknowledged: true, token: token).ConfigureAwait(false);
         return;
      }
      using HttpResponseMessage response = await _http.PostAsJsonAsync($"device/jobs/{Uri.EscapeDataString(jobId)}/events", printEvent, JsonOptions, token).ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
   }



   /// <summary>Releases the HTTP client.</summary>
   public void Dispose() => _http.Dispose();



   /// <summary>Lists jobs the server is holding for this device but this computer has not received. A server without the endpoint (404) is treated as having none, so an older server only loses the preview.</summary>
   public async Task<IReadOnlyList<PrintJob>> QueuedJobsAsync(CancellationToken token)
   {
      using HttpResponseMessage response = await _http.GetAsync("device/jobs/queued", token).ConfigureAwait(false);
      if(response.StatusCode == System.Net.HttpStatusCode.NotFound)
      { return []; }
      response.EnsureSuccessStatusCode();
      using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
      return result.RootElement.TryGetProperty("jobs", out JsonElement jobs) ? jobs.Deserialize<PrintJob[]>(JsonOptions) ?? [] : [];
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
