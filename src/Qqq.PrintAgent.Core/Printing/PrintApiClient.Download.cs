/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Security.Cryptography;

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// Streaming download part of the API client: documents go straight into a protected staging file while their size and SHA-256 are
/// checked, with total, connect and idle time limits and an optional shared bandwidth throttle. Nothing reaches the printer unless the
/// whole file verified.
/// </summary>
public sealed partial class PrintApiClient
{
   /// <summary>
   /// Downloads a job's document into a new uniquely named file in the staging folder and returns it open for reading. The
   /// settings must be valid and the declared size within the format and staging limits before any bytes are requested; a failed
   /// download deletes its partial file, so only a verified file is ever returned.
   /// </summary>
   public async Task<DownloadedDocument> DownloadFileAsync(PrintJob job, string directory, RealtimeSettings settings, CancellationToken token, Func<int, CancellationToken, Task>? throttle = null)
   {
      settings.Validate();
      if(job.ContentLength < 1 || job.ContentLength > settings.MaximumDocumentBytes(job.Format) || job.ContentLength > settings.MaxStagedBytes)
      {
         throw new InvalidDataException("Document exceeds the negotiated format or staging limit.");
      }
      Directory.CreateDirectory(directory);
      string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".document");
      try
      {
         await DownloadIntoFileAsync(job, path, settings, throttle, token).ConfigureAwait(false);
         return new DownloadedDocument(path, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
      }
      catch
      {
         File.Delete(path);
         throw;
      }
   }



   /// <summary>
   /// Requests the document (through the signed-in session when there is one) under a total time limit and a shorter connect limit,
   /// refuses a response whose declared length differs from the manifest, and copies it into the file with verification. The file is
   /// created exclusively, so an existing file can never be overwritten.
   /// </summary>
   private async Task DownloadIntoFileAsync(PrintJob job, string path, RealtimeSettings settings, Func<int, CancellationToken, Task>? throttle, CancellationToken token)
   {
      using var total = CancellationTokenSource.CreateLinkedTokenSource(token);
      total.CancelAfter(settings.DownloadTimeoutMillis);
      using var connect = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
      connect.CancelAfter(settings.DownloadConnectTimeoutMillis);
      string route = $"device/jobs/{Uri.EscapeDataString(job.JobId)}/content";
      using HttpResponseMessage response = _nativeSessionClient is null
         ? await _http.GetAsync(route, HttpCompletionOption.ResponseHeadersRead, connect.Token).ConfigureAwait(false)
         : await _nativeSessionClient.SendPrintingAsync(HttpMethod.Get, route, cancellationToken: connect.Token).ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
      if(response.Content.Headers.ContentLength is long declared && declared != job.ContentLength)
      {
         throw new InvalidDataException("Document length does not match its manifest.");
      }
      await using Stream source = await response.Content.ReadAsStreamAsync(total.Token).ConfigureAwait(false);
      await using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
      await CopyVerifiedAsync(source, output, job, settings, throttle, total.Token).ConfigureAwait(false);
      await output.FlushAsync(total.Token).ConfigureAwait(false);
   }



   /// <summary>
   /// Copies the stream to the file while hashing it, stopping with an error the moment more than the declared length arrives, when no
   /// data arrives within the idle limit, or when the final length or SHA-256 does not match. A shared throttle, or else the per-download
   /// rate limit, paces the copy so the aggregate bandwidth setting holds.
   /// </summary>
   private static async Task CopyVerifiedAsync(Stream source, Stream output, PrintJob job, RealtimeSettings settings, Func<int, CancellationToken, Task>? throttle, CancellationToken token)
   {
      using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      byte[] buffer = new byte[81920];
      long received = 0;
      long started = Stopwatch.GetTimestamp();
      while(true)
      {
         using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
         idle.CancelAfter(settings.DownloadIdleTimeoutMillis);
         int count = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
         if(count == 0)
         { break; }
         received += count;
         if(received > job.ContentLength)
         { throw new InvalidDataException("Document exceeds its declared length."); }
         digest.AppendData(buffer, 0, count);
         await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
         if(throttle is not null)
         { await throttle(count, token).ConfigureAwait(false); }
         else if(settings.MaxDownloadBytesPerSecond > 0)
         {
            TimeSpan remaining = TimeSpan.FromSeconds((double)received / settings.MaxDownloadBytesPerSecond) - Stopwatch.GetElapsedTime(started);
            if(remaining > TimeSpan.Zero)
            { await Task.Delay(remaining, token).ConfigureAwait(false); }
         }
      }
      if(received != job.ContentLength || !string.Equals(Convert.ToHexString(digest.GetHashAndReset()), job.ContentSha256, StringComparison.OrdinalIgnoreCase))
      {
         throw new InvalidDataException("Document integrity check failed.");
      }
   }
}
