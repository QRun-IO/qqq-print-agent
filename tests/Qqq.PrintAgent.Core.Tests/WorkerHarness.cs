/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// A print worker composed the way the service composes one, so a worker test runs the code the product runs. The API
/// client carries a realtime channel, so job events travel as acknowledged frames; documents are staged to a folder
/// and handed to the printer as files; and every hook the runtime supplies is present, each one permissive until a
/// test replaces it through <see cref="Options"/> or the two predicates. The fake server answers each JOB_EVENT
/// frame: it records the event and acknowledges it, or, when the test's hook throws for it, refuses it with an ERROR
/// frame, which fails that one delivery and leaves the connection up. A test that needs the connection itself to go
/// away calls <see cref="Drop"/> and, later, <see cref="Reconnect"/>.
/// </summary>
internal sealed class WorkerHarness : IAsyncDisposable
{
   private readonly CancellationTokenSource _stop = new();
   private readonly List<Link> _links = [];
   private readonly TimeProvider? _time;



   /// <summary>A download throttle that never makes a download wait, for a test that is not about pacing.</summary>
   internal static Task Unpaced(int bytes, CancellationToken token) => Task.CompletedTask;



   /// <summary>Builds the composition on the given clock, the system clock by default, and connects it.</summary>
   internal WorkerHarness(TimeProvider? time = null)
   {
      _time = time;
      Reconnect();
      Api = FakeSessionClient.ApiOver(Server, time).WithRealtime(_links[0].Channel, new RealtimeSettings(), () => _links[^1].Channel);
      Options = new PrintWorkerOptions
      {
         StagingDirectory = Staging.Path,
         Settings = new RealtimeSettings(),
         CurrentSettings = () => new RealtimeSettings(),
         CanSubmit = () => true,
         EventGate = EventGate,
         ClaimGate = ClaimGate,
         ThrottleDownloadAsync = Unpaced,
         CanClaim = () => true,
         ReserveDocumentAsync = (_, _) => Task.FromResult<IAsyncDisposable>(new FreeLease()),
         AcquireSubmissionAsync = (_, _) => Task.FromResult<IAsyncDisposable>(new FreeLease()),
         ActiveJobChanged = _ => { },
         TimeProvider = time ?? TimeProvider.System,
      };
   }



   /// <summary>The fake server: it hands out claims and documents over HTTP and receives job events from the channel.</summary>
   internal FakePrintServer Server { get; } = new();



   /// <summary>The journal the worker records in.</summary>
   internal RecordingJournal Journal { get; } = new();



   /// <summary>The printing platform the worker submits staged files to.</summary>
   internal FakePrinterBackend Printer { get; init; } = new();



   /// <summary>Every line the worker logged, in order.</summary>
   internal List<string> Log { get; } = [];



   /// <summary>The folder documents are staged in; a finished job leaves nothing in it.</summary>
   internal TempDirectory Staging { get; } = new();



   /// <summary>The gate that serializes event delivery, free unless a worker is delivering.</summary>
   internal SemaphoreSlim EventGate { get; } = new(1, 1);



   /// <summary>The gate that serializes claims, free unless a worker is claiming.</summary>
   internal SemaphoreSlim ClaimGate { get; } = new(1, 1);



   /// <summary>The API client the worker uses.</summary>
   internal PrintApiClient Api { get; }



   /// <summary>The hooks and limits handed to the next worker; a test replaces members with a <c>with</c> expression.</summary>
   internal PrintWorkerOptions Options { get; set; }



   /// <summary>Whether dispatch is allowed; true unless a test says otherwise.</summary>
   internal Func<bool> CanDispatch { get; set; } = () => true;



   /// <summary>Whether the connection is synchronized; true unless a test says otherwise.</summary>
   internal Func<bool> CanSynchronize { get; set; } = () => true;



   /// <summary>Builds a worker from the current parts, as the runtime builds one for each pass of a lane.</summary>
   internal PrintWorker Worker() => new(Api, Printer, Journal, Log.Add, () => CanDispatch(), () => CanSynchronize(), Options);



   /// <summary>Runs one pass of a new worker.</summary>
   internal Task<bool> RunOnceAsync(CancellationToken token = default) => Worker().RunOnceAsync(token);



   /// <summary>
   /// Loses the connection, as when the server or the network goes away: a send waiting for its acknowledgement fails,
   /// and every later send fails until <see cref="Reconnect"/>. It may be called from the server's hook.
   /// </summary>
   internal void Drop()
   {
      _links[^1].Dropped = true;
      _links[^1].Socket.Drop();
   }



   /// <summary>Opens a new connection, which the API client picks up through its provider as it does after the runtime reconnects.</summary>
   internal void Reconnect()
   {
      var socket = new ScriptedWebSocket();
      var channel = new PrintingRealtimeChannel(socket, new RealtimeSettings(), timeProvider: _time);
      var link = new Link(socket, channel);
      socket.Sent = frame => AnswerAsync(link, frame);
      _links.Add(link);
      link.Run = channel.RunAsync(_ => { }, _stop.Token);
   }



   /// <summary>
   /// Plays the server for one frame the worker sent. A job event is handed to the fake server, then acknowledged, or
   /// refused with an ERROR frame when the server's hook threw for it. An event that arrived on a connection the hook
   /// then dropped was received, and its answer is lost with the connection. Any other frame is one a worker has no
   /// business sending; it is noted for the test to find and gets no answer.
   /// </summary>
   private async Task AnswerAsync(Link link, JsonElement frame)
   {
      string type = frame.GetProperty("type").GetString()!;
      string messageId = frame.GetProperty("messageId").GetString()!;
      if(!string.Equals(type, "JOB_EVENT", StringComparison.Ordinal))
      {
         Server.Unexpected.Add("frame " + type);
         return;
      }
      JsonElement payload = frame.GetProperty("payload");
      try
      { await Server.ReceiveEventAsync(payload.GetProperty("jobId").GetString()!, payload.GetProperty("event").Deserialize<PrintEvent>(PrintApiClient.JsonOptions)!); }
      catch(HttpRequestException)
      {
         link.Socket.Deliver("ERROR", messageId, new { code = "UNAVAILABLE" });
         return;
      }
      if(!link.Dropped)
      { link.Socket.Deliver("ACK", "ack-" + messageId, new { acknowledgedMessageId = messageId }); }
   }



   /// <summary>Stops every connection, waits for each to end, and releases everything the composition holds.</summary>
   public async ValueTask DisposeAsync()
   {
      await _stop.CancelAsync();
      await Task.WhenAll(_links.Select(link => link.Run)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
      foreach(Link link in _links)
      { link.Channel.Dispose(); }
      Api.Dispose();
      Server.Dispose();
      Staging.Dispose();
      EventGate.Dispose();
      ClaimGate.Dispose();
      _stop.Dispose();
   }



   /// <summary>One connection: its socket, the channel over it, the channel's run, and whether the test dropped it.</summary>
   private sealed class Link(ScriptedWebSocket socket, PrintingRealtimeChannel channel)
   {
      internal ScriptedWebSocket Socket { get; } = socket;

      internal PrintingRealtimeChannel Channel { get; } = channel;

      internal Task Run { get; set; } = Task.CompletedTask;

      internal bool Dropped { get; set; }
   }



   /// <summary>A lease that holds nothing and releases nothing, for a hook a test does not observe.</summary>
   private sealed class FreeLease : IAsyncDisposable
   {
      /// <summary>Nothing to release.</summary>
      public ValueTask DisposeAsync() => ValueTask.CompletedTask;
   }
}
