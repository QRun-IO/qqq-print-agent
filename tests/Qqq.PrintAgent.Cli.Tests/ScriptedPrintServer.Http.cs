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
/// The HTTP side of the scripted server, behind the session client, with the counters and switches tests use to see and steer
/// the agent's HTTPS traffic: claims, the queued-job preview, operator actions, the printer catalog and session routes.
/// </summary>
internal sealed partial class ScriptedPrintServer
{
   /// <summary>Counts the claims that reached the server, whatever they were answered.</summary>
   internal int Claims;



   /// <summary>True when the newest answered claim found the queue empty, which is how a claim drain ends.</summary>
   internal volatile bool LastClaimWasEmpty;



   /// <summary>
   /// Counts what the realtime path must never do: a request to a polling or control route, and a claim made while the
   /// agent has no connection whose applied state the server acknowledged. Each is also refused, so it cannot pass unnoticed.
   /// </summary>
   internal int ForbiddenPolls;



   /// <summary>
   /// True from the moment the server acknowledges the agent's report of the applied state until the agent's next HELLO
   /// or a restart of the service. A claim outside that span is a violation. A drop of the socket by the server does not
   /// end the span, because a claim the agent had already sent when the socket died is not its fault.
   /// </summary>
   private volatile bool _claimsAllowed;



   /// <summary>Counts every HTTP request the agent makes, so a test can show that an evaluation touched no network.</summary>
   internal int Requests;



   /// <summary>
   /// Counts requests that reached this server's handler but were addressed to another origin. Each is refused. A service
   /// with several servers gives every session the handler of the server it is for; a request here for another address
   /// means a session was built on the wrong server's handler.
   /// </summary>
   internal int MisroutedRequests;



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



   /// <summary>Controls advertised discovery across a server upgrade or downgrade while retaining the same authenticated client session.</summary>
   internal string PrintingVersion { get; set; } = "2.0.0";



   /// <summary>Printer identifiers the fake server's operator catalog lists for this device; both shared queues by default.</summary>
   internal string[] ApprovedPrinters { get; set; } = ["printer", "other"];



   /// <summary>When set, the operator catalog answers with this status instead of a page, to model an older server (404) or a user without catalog access (403).</summary>
   internal HttpStatusCode? CatalogStatus { get; set; }



   /// <summary>
   /// Fake HTTP server for the session client: version discovery (advertising native sign-in and the configured printing
   /// version), document content, claims, the queued list, operator actions, enrollment and session identity. Any other route
   /// is a forbidden polling or control call and is counted and thrown, which proves the realtime path adds no HTTP polling.
   /// </summary>
   private sealed class Handler(ScriptedPrintServer server) : HttpMessageHandler
   {
      /// <summary>Counts the request, refuses and counts one addressed to another server, applies a scripted fault, and routes by path.</summary>
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         Interlocked.Increment(ref server.Requests);
         if(!string.Equals(request.RequestUri!.Authority, server.Origin.Authority, StringComparison.OrdinalIgnoreCase))
         {
            Interlocked.Increment(ref server.MisroutedRequests);
            throw new InvalidOperationException("Request for " + request.RequestUri.Authority + " reached the scripted server at " + server.Origin.Authority + ".");
         }
         if(server.HttpFault?.Invoke(request) is Exception fault)
         { throw fault; }
         string path = request.RequestUri.AbsolutePath;
         if(string.Equals(path, "/qqq/v1/version", StringComparison.Ordinal))
         {
            Interlocked.Increment(ref server.DiscoveryRequests);
            return Response(JsonSerializer.Serialize(new
            {
               apiVersion = "1.9.0",
               features = new[] { "version", "metaData", "processes", "processCancel", "extensions", "widgets", "nativeSessionLogin" },
               clientChannels = new[] { "api" },
               extensions = new[] { new { name = "printing", version = server.PrintingVersion, pathPrefix = "/qbit/printing" } },
            }));
         }
         if(path.EndsWith("/content", StringComparison.Ordinal))
         {
            string id = path.Split('/')[^2];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(server.Documents[id]()) });
         }
         if(path.EndsWith("/claim", StringComparison.Ordinal))
         { return ClaimAsync(cancellationToken); }
         return TryJobRoute(path, out Task<HttpResponseMessage>? response) ? response : SessionRoute(request, path);
      }



      /// <summary>
      /// Refuses and counts a claim made without an acknowledged connection. Otherwise counts the claim, waits while claims are
      /// held, then hands out the queue head under a lock so a job is claimed once until its RECEIVED event arrives.
      /// </summary>
      private async Task<HttpResponseMessage> ClaimAsync(CancellationToken cancellationToken)
      {
         if(!server._claimsAllowed)
         {
            Interlocked.Increment(ref server.ForbiddenPolls);
            throw new InvalidOperationException("Claim without a realtime connection whose applied state was acknowledged.");
         }
         Interlocked.Increment(ref server.Claims);
         server.ClaimArrived.TrySetResult();
         if(server.ClaimHold is TaskCompletionSource hold)
         { await hold.Task.WaitAsync(cancellationToken); }
         lock(server._claimLock)
         {
            if(server._claimed is null)
            { server.Queue.TryDequeue(out server._claimed); }
            server.LastClaimWasEmpty = server._claimed is null;
            if(server._claimed is null && server.FailEmptyClaims)
            { return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { job = server._claimed }, PrintApiClient.JsonOptions)) };
         }
      }



      /// <summary>Answers the queued-job preview, the operator actions (test print, cancel) and the operator printer catalog; false when the path is none of them.</summary>
      private bool TryJobRoute(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Task<HttpResponseMessage>? response)
      {
         response = null;
         if(path.EndsWith("/queued", StringComparison.Ordinal))
         {
            response = Response(JsonSerializer.Serialize(new { jobs = server.Queue.ToArray() }, PrintApiClient.JsonOptions));
         }
         else if(path.EndsWith("/device/test-print", StringComparison.Ordinal))
         {
            server.OperatorActions.Enqueue(path);
            response = Response("""{"jobId":"test-queued"}""");
         }
         else if(path.EndsWith("/cancel", StringComparison.Ordinal))
         {
            server.OperatorActions.Enqueue(path);
            Interlocked.Increment(ref server.OperatorCancellations);
            server.RemoveQueued(path.Split('/')[^2]);
            response = Response("""{"accepted":true}""");
         }
         else if(path.EndsWith("/operator/printers", StringComparison.Ordinal))
         {
            Interlocked.Increment(ref server.CatalogRequests);
            response = server.CatalogStatus is HttpStatusCode status
               ? Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") })
               : Response(JsonSerializer.Serialize(new
               {
                  printers = server.ApprovedPrinters.Select(id => new { printerRowId = "row-" + id, deviceId = "device", printerId = id, name = id, deviceName = "Synthetic workstation", status = "READY", formats = new[] { "PDF" }, mediaProfiles = Array.Empty<object>() }),
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
         Interlocked.Increment(ref server.ForbiddenPolls);
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
