/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// A hand-written fake of the device-facing print API for worker tests. It hands out the jobs a test offers (as raw
/// JSON, so a malformed claim can be offered too), serves one document, and records every event, heartbeat and
/// inventory it receives. A hook runs before each answer so a test can fail a request or move a clock at that point.
/// </summary>
internal sealed class FakePrintServer : HttpMessageHandler
{
   private readonly Queue<string> _claims = new();



   /// <summary>The document served for any content request.</summary>
   internal byte[] Document { get; set; } = [];



   /// <summary>Every event the worker delivered, in order.</summary>
   internal List<(string JobId, PrintEvent Event)> Events { get; } = [];



   /// <summary>The status of every delivered event, in order.</summary>
   internal IEnumerable<string> States => Events.Select(entry => entry.Event.Status);



   /// <summary>How many claim requests arrived.</summary>
   internal int Claims { get; private set; }



   /// <summary>How many heartbeats arrived.</summary>
   internal int Heartbeats { get; private set; }



   /// <summary>How many inventory reports arrived.</summary>
   internal int Inventories { get; private set; }



   /// <summary>How many document requests arrived.</summary>
   internal int Downloads { get; private set; }



   /// <summary>Runs before each answer with the kind of request ("claim", "content", "events", "heartbeat", "printers" or "other"); it may throw to fail that request.</summary>
   internal Func<string, CancellationToken, Task>? Arriving { get; set; }



   /// <summary>A synthetic ZPL job for printer1 whose length and hash match the given document. The hash is lowercase hexadecimal, as the contract writes it.</summary>
   internal static PrintJob Job(byte[] document, string jobId = "job-1", PrintOptions? options = null, string format = "ZPL")
      => new(jobId, "printer1", "Synthetic", format, Convert.ToHexStringLower(SHA256.HashData(document)), document.Length, options ?? new PrintOptions(), "2026-01-01T00:00:00Z", "CLAIMED");



   /// <summary>Offers a job to the next claim and serves its document.</summary>
   internal FakePrintServer Offer(PrintJob job, byte[] document)
   {
      Document = document;
      return OfferRaw(JsonSerializer.Serialize(new JobClaim(job), PrintApiClient.JsonOptions));
   }



   /// <summary>Answers the next claim with exactly this body, for claims a well-behaved server would never send.</summary>
   internal FakePrintServer OfferRaw(string body)
   {
      _claims.Enqueue(body);
      return this;
   }



   /// <summary>Counts the request by route, runs the hook, records an event, and answers a claim with the next offered body or an empty claim.</summary>
   protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
   {
      string path = request.RequestUri!.AbsolutePath;
      string kind = path.EndsWith("/claim", StringComparison.Ordinal) ? "claim"
         : path.EndsWith("/content", StringComparison.Ordinal) ? "content"
         : path.EndsWith("/events", StringComparison.Ordinal) ? "events"
         : path.EndsWith("/heartbeat", StringComparison.Ordinal) ? "heartbeat"
         : path.EndsWith("/printers", StringComparison.Ordinal) ? "printers" : "other";
      if(Arriving is not null)
      { await Arriving(kind, cancellationToken); }
      switch(kind)
      {
         case "claim":
            Claims++;
            return Json(_claims.Count > 0 ? _claims.Dequeue() : """{"job":null}""");
         case "content":
            Downloads++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Document) };
         case "events":
            string jobId = Uri.UnescapeDataString(path.Split('/')[^2]);
            Events.Add((jobId, JsonSerializer.Deserialize<PrintEvent>(await request.Content!.ReadAsStringAsync(cancellationToken), PrintApiClient.JsonOptions)!));
            break;
         case "heartbeat":
            Heartbeats++;
            break;
         case "printers":
            Inventories++;
            break;
      }
      return Json("{}");
   }



   /// <summary>A 200 JSON response.</summary>
   private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}



/// <summary>
/// An in-memory stand-in for the durable job journal with the recording, acknowledgement and incomplete-job semantics the
/// worker relies on. It keeps every event in order so a test can assert the exact sequence of states a job went through.
/// </summary>
internal sealed class RecordingJournal : IJobJournal
{
   private readonly List<(string JobId, PrintEvent Event)> _events = [];
   private readonly HashSet<string> _acknowledged = new(StringComparer.Ordinal);



   /// <summary>The jobs the worker registered, with the printer name shown for each.</summary>
   internal Dictionary<string, (PrintJob Job, string? PrinterName)> Jobs { get; } = new(StringComparer.Ordinal);



   /// <summary>Runs after each event is recorded; a test uses it to interrupt the worker at an exact state.</summary>
   internal Action<string, PrintEvent>? Recorded { get; set; }



   /// <summary>Every state recorded for a job, in order.</summary>
   internal string[] StatesOf(string jobId) => [.. _events.Where(entry => string.Equals(entry.JobId, jobId, StringComparison.Ordinal)).Select(entry => entry.Event.Status)];



   /// <summary>The most recent event recorded for a job.</summary>
   internal PrintEvent Last(string jobId) => _events.Last(entry => string.Equals(entry.JobId, jobId, StringComparison.Ordinal)).Event;



   /// <summary>Remembers the job and the printer name shown for it.</summary>
   public void TrackJob(PrintJob job, string? printerName) => Jobs[job.JobId] = (job, printerName);



   /// <summary>The status of the most recent event recorded for the job, or null when there is none.</summary>
   public string? GetState(string jobId) => _events.LastOrDefault(entry => string.Equals(entry.JobId, jobId, StringComparison.Ordinal)).Event?.Status;



   /// <summary>Appends an event, unacknowledged until the server confirms it, then runs the test's hook.</summary>
   public void Record(string jobId, PrintEvent printEvent)
   {
      _events.Add((jobId, printEvent));
      Recorded?.Invoke(jobId, printEvent);
   }



   /// <summary>The most undelivered events one call returns, as the durable journal hands them out in bounded batches; unlimited by default.</summary>
   internal int BatchLimit { get; set; } = int.MaxValue;



   /// <summary>Events the server has not yet acknowledged, in the order they were recorded, at most one batch.</summary>
   public IReadOnlyList<(string JobId, PrintEvent Event)> Pending() => [.. _events.Where(entry => !_acknowledged.Contains(entry.Event.EventId)).Take(BatchLimit)];



   /// <summary>Marks the event as delivered so it is no longer pending.</summary>
   public void Acknowledge(string eventId) => _acknowledged.Add(eventId);



   /// <summary>Jobs whose latest event is RECEIVED or SUBMITTING, meaning a run stopped before finishing them.</summary>
   public IReadOnlyList<(string JobId, string State)> Incomplete() => [.. _events.GroupBy(entry => entry.JobId, StringComparer.Ordinal).Select(group => group.Last())
      .Where(entry => entry.Event.Status is "RECEIVED" or "SUBMITTING").Select(entry => (entry.JobId, entry.Event.Status))];
}



/// <summary>
/// A printing platform that prints nothing: it lists the printers a test gives it and counts every submission that
/// reached the native step, with hooks at discovery and at submission so a test can change the queue or fail the print
/// at that exact point.
/// </summary>
internal class FakePrinterBackend : IPrinterBackend
{
   /// <summary>The printers reported by discovery; one ready ZPL and PDF queue named printer1 by default.</summary>
   internal List<PrinterInfo> Printers { get; } = [new("printer1", "Queue one", "Synthetic driver", "USB001", "READY", ["ZPL", "PDF"])];



   /// <summary>How many times discovery ran.</summary>
   internal int Discoveries { get; private set; }



   /// <summary>How many submissions reached the native step, whether they then succeeded or failed.</summary>
   internal int Submissions { get; private protected set; }



   /// <summary>The content of every in-memory submission.</summary>
   internal List<byte[]> Printed { get; } = [];



   /// <summary>Runs at the start of each discovery.</summary>
   internal Action? Discovering { get; set; }



   /// <summary>Replaces the in-memory submission; returns the spooler receipt or throws.</summary>
   internal Func<PrintJob, CancellationToken, Task<string>>? Printing { get; set; }



   /// <summary>Counts the discovery, runs the hook and returns a copy of the configured printers.</summary>
   public IReadOnlyList<PrinterInfo> Discover()
   {
      Discoveries++;
      Discovering?.Invoke();
      return [.. Printers];
   }



   /// <summary>Counts the submission and keeps its content, then defers to the hook or returns a fixed spooler receipt.</summary>
   public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
   {
      Submissions++;
      Printed.Add(content);
      return Printing is null ? Task.FromResult("spool-1") : Printing(job, cancellationToken);
   }
}



/// <summary>A printing platform that also takes staged files, recording the file's content and the submission limit it was given.</summary>
internal sealed class FakeFilePrinterBackend : FakePrinterBackend, IFilePrinterBackend
{
   /// <summary>The content and submission limit of every file submission.</summary>
   internal List<(byte[] Content, TimeSpan Limit)> Files { get; } = [];



   /// <summary>Replaces the file submission; returns the spooler receipt or throws.</summary>
   internal Func<PrintJob, string, CancellationToken, Task<string>>? PrintingFile { get; set; }



   /// <summary>The overload without a limit is never the one the worker should use.</summary>
   public Task<string> PrintFileAsync(PrintJob job, string path, CancellationToken cancellationToken) => throw new InvalidOperationException("The worker must pass the submission limit.");



   /// <summary>Counts the submission and reads the staged file as the spooler would, then defers to the hook or returns a fixed receipt.</summary>
   public Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken)
   {
      Submissions++;
      Files.Add((File.ReadAllBytes(path), submissionTimeout));
      return PrintingFile is null ? Task.FromResult("spool-file-1") : PrintingFile(job, path, cancellationToken);
   }
}
