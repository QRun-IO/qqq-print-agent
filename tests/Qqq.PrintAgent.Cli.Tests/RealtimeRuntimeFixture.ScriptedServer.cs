/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;
using System.Net;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Testing;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The fixture's one scripted server, under the names tests have always used for it. Each member here only passes through to
/// <see cref="Server"/>: with one server, "the fixture's queue" and "the server's queue" are the same thing, and a test reads
/// better without the extra word. A fixture with several servers has no such members; its tests name the server.
/// </summary>
internal sealed partial class RealtimeRuntimeFixture
{
   /// <summary>The loopback listener the agent's WebSocket connects to; its origin is the server address saved in the store.</summary>
   internal LoopbackWebSocketPeer Peer => Server.Peer;



   /// <summary>The realtime settings the server sends in WELCOME: short reconnect delays without jitter, everything else at its default.</summary>
   internal RealtimeSettings Settings { get => Server.Settings; set => Server.Settings = value; }



   /// <summary>The server's queue of jobs waiting to be claimed, oldest first.</summary>
   internal ConcurrentQueue<PrintJob> Queue => Server.Queue;



   /// <summary>How each job's document is served when the agent downloads it, by job id.</summary>
   internal ConcurrentDictionary<string, Func<Stream>> Documents => Server.Documents;



   /// <summary>Every message the agent sent on any connection, in the order the server received them.</summary>
   internal ConcurrentQueue<RealtimeEnvelope> Messages => Server.Messages;



   /// <summary>When set, the server holds a RECEIVED event (and so the claimed job) until the test completes it.</summary>
   internal TaskCompletionSource? ReceivedRelease { get => Server.ReceivedRelease; set => Server.ReceivedRelease = value; }



   /// <summary>When set, the server holds its acknowledgement of STATE_APPLIED until the test completes it, which keeps the agent from becoming ready.</summary>
   internal TaskCompletionSource? StateAppliedRelease { get => Server.StateAppliedRelease; set => Server.StateAppliedRelease = value; }



   /// <summary>Runs for every agent message before the server answers it; a test awaits inside it to hold one acknowledgement back.</summary>
   internal Func<RealtimeEnvelope, Task>? BeforeAcknowledgement { get => Server.BeforeAcknowledgement; set => Server.BeforeAcknowledgement = value; }



   /// <summary>The settings revision the agent reported in its latest HELLO.</summary>
   internal long LastHelloSettingsRevision => Server.LastHelloSettingsRevision;



   /// <summary>Returns an error code to answer an agent message with a realtime ERROR frame instead of an acknowledgement (NOT_FOUND models a server that no longer knows the record), or null to acknowledge it.</summary>
   internal Func<RealtimeEnvelope, string?>? RefuseWith { get => Server.RefuseWith; set => Server.RefuseWith = value; }



   /// <summary>When true, the server closes the socket with a close frame straight after refusing a message, as a server that gives up on a misbehaving client does.</summary>
   internal bool CloseAfterRefusal { get => Server.CloseAfterRefusal; set => Server.CloseAfterRefusal = value; }



   /// <summary>Returns true for an agent message the server must receive and then drop the connection without acknowledging, as a network failure at that exact moment would.</summary>
   internal Func<RealtimeEnvelope, bool>? LoseAcknowledgementOf { get => Server.LoseAcknowledgementOf; set => Server.LoseAcknowledgementOf = value; }



   /// <summary>When true the server sends a desired state again when the agent reports an older revision as applied; when false it stays silent, which shows what a lost frame costs when nothing repairs it.</summary>
   internal bool ResendUnappliedState { get => Server.ResendUnappliedState; set => Server.ResendUnappliedState = value; }



   /// <summary>Captures the actual HELLO payload so compatibility tests observe the wire rather than duplicating capability selection.</summary>
   internal string[] LastHelloCapabilities => Server.LastHelloCapabilities;



   /// <summary>Controls advertised discovery across a server upgrade or downgrade while retaining the same authenticated client session.</summary>
   internal string PrintingVersion { get => Server.PrintingVersion; set => Server.PrintingVersion = value; }



   /// <summary>Printer identifiers the fake server's operator catalog lists for this device; both shared queues by default.</summary>
   internal string[] ApprovedPrinters { get => Server.ApprovedPrinters; set => Server.ApprovedPrinters = value; }



   /// <summary>When set, the operator catalog answers with this status instead of a page, to model an older server (404) or a user without catalog access (403).</summary>
   internal HttpStatusCode? CatalogStatus { get => Server.CatalogStatus; set => Server.CatalogStatus = value; }



   /// <summary>Counts the claims that reached the server, whatever they were answered.</summary>
   internal int Claims => Server.Claims;



   /// <summary>True when the newest answered claim found the queue empty, which is how a claim drain ends.</summary>
   internal bool LastClaimWasEmpty => Server.LastClaimWasEmpty;



   /// <summary>
   /// Counts what the realtime path must never do: a request to a polling or control route, and a claim made while the
   /// agent has no connection whose applied state the server acknowledged. Each is also refused, so it cannot pass unnoticed.
   /// </summary>
   internal int ForbiddenPolls => Server.ForbiddenPolls;



   /// <summary>Counts every HTTP request the agent makes, so a test can show that an evaluation touched no network.</summary>
   internal int Requests => Server.Requests;



   /// <summary>Counts discovery requests to distinguish reconnect reuse from an added HTTP polling schedule.</summary>
   internal int DiscoveryRequests => Server.DiscoveryRequests;



   /// <summary>Counts operator catalog reads.</summary>
   internal int CatalogRequests => Server.CatalogRequests;



   /// <summary>Counts job cancellations the agent asked the server for as an operator action.</summary>
   internal int OperatorCancellations => Server.OperatorCancellations;



   /// <summary>The operator action routes the agent called, in order.</summary>
   internal ConcurrentQueue<string> OperatorActions => Server.OperatorActions;



   /// <summary>When set, every claim waits for it before it is answered, which holds a claim drain open at the claim itself.</summary>
   internal TaskCompletionSource? ClaimHold { get => Server.ClaimHold; set => Server.ClaimHold = value; }



   /// <summary>Completed when the first claim reaches the server, whether or not it is being held.</summary>
   internal TaskCompletionSource ClaimArrived => Server.ClaimArrived;



   /// <summary>When true, a claim that finds the queue empty is answered 503 instead of "no job", as a failing server would.</summary>
   internal bool FailEmptyClaims { get => Server.FailEmptyClaims; set => Server.FailEmptyClaims = value; }



   /// <summary>Returns an exception to throw for a request instead of answering it, to model a request that never reaches the server; null answers normally.</summary>
   internal Func<HttpRequestMessage, Exception?>? HttpFault { get => Server.HttpFault; set => Server.HttpFault = value; }



   /// <summary>Queues a synthetic PDF job on the server and registers how its document is served; see <see cref="ScriptedPrintServer.AddJob"/>.</summary>
   internal void AddJob(string id, Func<Stream>? content = null, string printerId = "printer", long? length = null) => Server.AddJob(id, content, printerId, length);



   /// <summary>How many messages of one type the agent has sent so far.</summary>
   internal int Count(string type) => Server.Count(type);
}
