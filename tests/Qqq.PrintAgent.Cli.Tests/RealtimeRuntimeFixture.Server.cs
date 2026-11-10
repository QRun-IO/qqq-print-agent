/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The scripted HTTP server behind the fixture's session client, with the counters and switches tests use to see and steer the
/// agent's HTTPS traffic: claims, the queued-job preview, operator actions, the printer catalog and session routes.
/// </summary>
internal sealed partial class RealtimeRuntimeFixture
{
   internal int Claims;
   internal volatile bool LastClaimWasEmpty;
   internal int ForbiddenPolls;



   /// <summary>Counts every HTTP request the agent makes, so a test can show that an evaluation touched no network.</summary>
   internal int Requests;



   /// <summary>Counts discovery requests to distinguish reconnect reuse from an added HTTP polling schedule.</summary>
   internal int DiscoveryRequests;



   /// <summary>Counts operator catalog reads.</summary>
   internal int CatalogRequests;



   /// <summary>Counts job cancellations the agent asked the server for as an operator action.</summary>
   internal int OperatorCancellations;



   /// <summary>The operator action routes the agent called, in order.</summary>
   internal ConcurrentQueue<string> OperatorActions { get; } = new();



   /// <summary>When set, every claim waits for it before it is answered, which holds a claim drain open at the claim itself.</summary>
   internal TaskCompletionSource? ClaimHold { get; set; }



   /// <summary>Completed when the first claim reaches the server, whether or not it is being held.</summary>
   internal TaskCompletionSource ClaimArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);



   /// <summary>When true, a claim that finds the queue empty is answered 503 instead of "no job", as a failing server would.</summary>
   internal bool FailEmptyClaims { get; set; }



   /// <summary>Returns an exception to throw for a request instead of answering it, to model a request that never reaches the server; null answers normally.</summary>
   internal Func<HttpRequestMessage, Exception?>? HttpFault { get; set; }



   /// <summary>
   /// Fake HTTP server for the session client: version discovery (advertising native sign-in and the configured printing
   /// version), document content, claims, the queued list, operator actions, enrollment and session identity. Any other route
   /// is a forbidden polling or control call and is counted and thrown, which proves the realtime path adds no HTTP polling.
   /// </summary>
   private sealed class Handler(RealtimeRuntimeFixture fixture) : HttpMessageHandler
   {
      /// <summary>Counts the request, applies a scripted fault, and routes by path.</summary>
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         Interlocked.Increment(ref fixture.Requests);
         if(fixture.HttpFault?.Invoke(request) is Exception fault)
         { throw fault; }
         string path = request.RequestUri!.AbsolutePath;
         if(string.Equals(path, "/qqq/v1/version", StringComparison.Ordinal))
         {
            Interlocked.Increment(ref fixture.DiscoveryRequests);
            return Response(JsonSerializer.Serialize(new
            {
               apiVersion = "1.9.0",
               features = new[] { "version", "metaData", "processes", "processCancel", "extensions", "widgets", "nativeSessionLogin" },
               clientChannels = new[] { "api" },
               extensions = new[] { new { name = "printing", version = fixture.PrintingVersion, pathPrefix = "/qbit/printing" } },
            }));
         }
         if(path.EndsWith("/content", StringComparison.Ordinal))
         {
            string id = path.Split('/')[^2];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(fixture.Documents[id]()) });
         }
         if(path.EndsWith("/claim", StringComparison.Ordinal))
         { return ClaimAsync(cancellationToken); }
         return TryJobRoute(path, out Task<HttpResponseMessage>? response) ? response : SessionRoute(request, path);
      }



      /// <summary>Counts the claim, waits while claims are held, then hands out the queue head under a lock so a job is claimed once until its RECEIVED event arrives.</summary>
      private async Task<HttpResponseMessage> ClaimAsync(CancellationToken cancellationToken)
      {
         Interlocked.Increment(ref fixture.Claims);
         fixture.ClaimArrived.TrySetResult();
         if(fixture.ClaimHold is TaskCompletionSource hold)
         { await hold.Task.WaitAsync(cancellationToken); }
         lock(fixture._claimLock)
         {
            if(fixture._claimed is null)
            { fixture.Queue.TryDequeue(out fixture._claimed); }
            fixture.LastClaimWasEmpty = fixture._claimed is null;
            if(fixture._claimed is null && fixture.FailEmptyClaims)
            { return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { job = fixture._claimed }, PrintApiClient.JsonOptions)) };
         }
      }



      /// <summary>Answers the queued-job preview, the operator actions (test print, cancel) and the operator printer catalog; false when the path is none of them.</summary>
      private bool TryJobRoute(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Task<HttpResponseMessage>? response)
      {
         response = null;
         if(path.EndsWith("/queued", StringComparison.Ordinal))
         {
            response = Response(JsonSerializer.Serialize(new { jobs = fixture.Queue.ToArray() }, PrintApiClient.JsonOptions));
         }
         else if(path.EndsWith("/device/test-print", StringComparison.Ordinal))
         {
            fixture.OperatorActions.Enqueue(path);
            response = Response("""{"jobId":"test-queued"}""");
         }
         else if(path.EndsWith("/cancel", StringComparison.Ordinal))
         {
            fixture.OperatorActions.Enqueue(path);
            Interlocked.Increment(ref fixture.OperatorCancellations);
            fixture.RemoveQueued(path.Split('/')[^2]);
            response = Response("""{"accepted":true}""");
         }
         else if(path.EndsWith("/operator/printers", StringComparison.Ordinal))
         {
            Interlocked.Increment(ref fixture.CatalogRequests);
            response = fixture.CatalogStatus is HttpStatusCode status
               ? Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") })
               : Response(JsonSerializer.Serialize(new
               {
                  printers = fixture.ApprovedPrinters.Select(id => new { printerRowId = "row-" + id, deviceId = "device", printerId = id, name = id, deviceName = "Synthetic workstation", status = "READY", formats = new[] { "PDF" }, mediaProfiles = Array.Empty<object>() }),
                  nextCursor = (string?)null,
               }, PrintApiClient.JsonOptions));
         }
         return response is not null;
      }



      /// <summary>Enrollment, the printing session identity and the framework's session routes; anything else is counted as a forbidden polling or control route.</summary>
      private Task<HttpResponseMessage> SessionRoute(HttpRequestMessage request, string path)
      {
         if(path.EndsWith("/devices/enroll", StringComparison.Ordinal))
         { return Response("""{"protocolVersion":1,"deviceId":"replacement-device","deviceToken":"replacement-proof","pollIntervalSeconds":5}"""); }
         if(path.EndsWith("/printing/session", StringComparison.Ordinal))
         {
            string deviceId = request.Headers.TryGetValues("X-QQQ-Device-Id", out IEnumerable<string>? ids) ? ids.Single() : "device";
            return Response(JsonSerializer.Serialize(new { principalId = "user", tenantId = "tenant", deviceId }));
         }
         return Response(path switch
         {
            "/qqq/v1/manageSession" => """{"uuid":"synthetic-session"}""",
            "/qqq/v1/metaData/authentication" => """{"name":"local","type":"TABLE_BASED"}""",
            _ => Unexpected(path),
         });
      }



      /// <summary>Counts and rejects a route the realtime path must never call.</summary>
      private string Unexpected(string path)
      {
         Interlocked.Increment(ref fixture.ForbiddenPolls);
         throw new InvalidOperationException("Unexpected HTTP polling/control route: " + path);
      }



      /// <summary>A 200 response with the given text body.</summary>
      private static Task<HttpResponseMessage> Response(string value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(value) });
   }



   /// <summary>Takes a job out of the server queue, as the server does when an operator cancels it before dispatch.</summary>
   private void RemoveQueued(string jobId)
   {
      lock(_claimLock)
      {
         PrintJob[] remaining = Queue.Where(job => !string.Equals(job.JobId, jobId, StringComparison.Ordinal)).ToArray();
         Queue.Clear();
         foreach(PrintJob job in remaining)
         { Queue.Enqueue(job); }
      }
   }
}
