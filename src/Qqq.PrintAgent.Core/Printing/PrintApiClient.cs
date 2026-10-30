/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qqq.PrintAgent.Core.Printing;

public sealed class PrintApiClient : IDisposable
{
   public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
   private readonly HttpClient _http;
   public static string AgentVersion => typeof(PrintApiClient).Assembly.GetName().Version?.ToString() ?? "unknown";

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
      if(token is not null)
      {
         _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
      }
   }

   public async Task<EnrollmentResponse> EnrollAsync(EnrollmentRequest request, CancellationToken token = default)
   {
      using HttpResponseMessage response = await _http.PostAsJsonAsync("enroll", request, JsonOptions, token).ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
      EnrollmentResponse result = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(token).ConfigureAwait(false)
         ?? throw new InvalidDataException("Missing enrollment response.");
      if(result.ProtocolVersion != 1)
      {
         throw new InvalidDataException("Unsupported printing protocol.");
      }

      return result;
   }

   public async Task InventoryAsync(IReadOnlyList<PrinterInfo> printers, CancellationToken token)
   {
      using HttpResponseMessage response = await _http.PutAsJsonAsync("device/printers", new { printers }, JsonOptions, token).ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
   }

   public Task HeartbeatAsync(CancellationToken token) => HeartbeatAsync(null, token);

   public async Task HeartbeatAsync(DeviceProfile? profile, CancellationToken token)
   {
      using HttpResponseMessage response = await _http.PostAsJsonAsync("device/heartbeat", new { agentVersion = AgentVersion, profile }, JsonOptions, token).ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
   }

   public async Task<PrintJob?> ClaimAsync(CancellationToken token)
   {
      using HttpResponseMessage response = await _http.PostAsJsonAsync("device/jobs/claim", new { }, JsonOptions, token).ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
      return (await response.Content.ReadFromJsonAsync<JobClaim>(token).ConfigureAwait(false))?.Job;
   }

   public async Task<byte[]> DownloadAsync(PrintJob job, CancellationToken token)
   {
      if(job.ContentLength is < 1 or > 20_971_520)
      {
         throw new InvalidDataException("Document exceeds the 20 MiB limit or is empty.");
      }

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

   public async Task EventAsync(string jobId, PrintEvent printEvent, CancellationToken token)
   {
      using HttpResponseMessage response = await _http.PostAsJsonAsync($"device/jobs/{Uri.EscapeDataString(jobId)}/events", printEvent, JsonOptions, token).ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
   }

   public void Dispose() => _http.Dispose();

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





