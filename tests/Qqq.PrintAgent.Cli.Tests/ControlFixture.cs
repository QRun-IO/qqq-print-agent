/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Shared test harness for remote-control behavior: an enrolled agent state in a temporary folder, a scriptable fake server
/// (control poll, receipts, heartbeat, inventory, claim) and a synthetic printer that can block or fail cancellation. Tests set
/// the scripted responses, then read the counters to see exactly what the runtime did.
/// </summary>
internal sealed class ControlFixture : IDisposable
{
   internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "qqq-control-" + Guid.NewGuid().ToString("N"));
   internal DeviceDesiredState Desired { get; set; } = new(1, false, "CONNECTED");
   internal DeviceControlCommand[] Commands { get; set; } = [];
   internal HttpStatusCode PollStatus { get; set; } = HttpStatusCode.OK;
   internal string? PollBody { get; set; }
   internal int ReceiptFailures { get; set; }
   internal HttpStatusCode ReceiptStatus { get; set; } = HttpStatusCode.OK;
   internal int Polls;
   internal int Inventories;
   internal int Heartbeats;
   internal int Claims;
   internal List<string> Receipts { get; } = [];
   internal SyntheticPrinter Printer { get; } = new();
   internal bool OfferJob { get; set; }
   internal static byte[] Content => [1, 2, 3];



   /// <summary>Saves an enrolled configuration and shares the synthetic queue, so the runtime starts in a connected, ready state.</summary>
   internal ControlFixture()
   {
      using AgentStore store = new(DirectoryPath);
      store.SaveConfiguration(new AgentConfiguration("https://synthetic.invalid", "device", "synthetic-token"));
      store.SetExposure("printer", true, Printer.Discover());
   }



   /// <summary>Builds a runtime bound to this fixture's fake server on a unique private pipe, with 30 ms control and heartbeat intervals so tests finish quickly.</summary>
   internal AgentRuntime Runtime()
   {
      string pipe = "qqq-control-test-" + Guid.NewGuid().ToString("N");
      return new AgentRuntime(DirectoryPath, _ => { }, Printer, config => new PrintApiClient(config.ServerUrl, config.DeviceToken, new Handler(this)), pipe,
         () => new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
      { ControlIntervalOverride = TimeSpan.FromMilliseconds(30), HeartbeatInterval = TimeSpan.FromMilliseconds(30) };
   }



   /// <summary>Builds a server command, by default an unexpired cancel of the seeded job; expired shifts both timestamps into the past so staleness handling can be tested.</summary>
   internal static DeviceControlCommand Command(string action = "CANCEL_SPOOL", string id = "command", bool expired = false) => new(id, "request-" + id, action,
      DateTimeOffset.UtcNow.AddMinutes(expired ? -20 : -1).ToString("O", CultureInfo.InvariantCulture),
      DateTimeOffset.UtcNow.AddMinutes(expired ? -10 : 10).ToString("O", CultureInfo.InvariantCulture), string.Equals(action, "CANCEL_SPOOL", StringComparison.Ordinal) ? "job" : null);



   /// <summary>Records a job that has already been handed to the Windows spooler (spooler id 123) with its receipt acknowledged, the state a cancel command acts on.</summary>
   internal void SeedSpooledJob()
   {
      using AgentStore store = new(DirectoryPath);
      store.TrackJob(Job(), "Synthetic printer");
      PrintEvent receipt = new("spooled", "SPOOLED", SpoolerJobId: "123");
      store.Record("job", receipt);
      store.Acknowledge(receipt.EventId);
   }



   /// <summary>The one synthetic PDF job, with a hash that matches the fixture's three content bytes.</summary>
   private static PrintJob Job() => new("job", "printer", "Synthetic", "PDF", Convert.ToHexString(SHA256.HashData(Content)), Content.Length,
      new PrintOptions(), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), "CLAIMED");



   /// <summary>Releases any blocked print, clears SQLite's pool so the files unlock, and deletes the temporary state folder.</summary>
   public void Dispose()
   {
      Printer.Release.TrySetResult();
      Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
      Directory.Delete(DirectoryPath, recursive: true);
   }



   /// <summary>
   /// A printer and spooler-control stand-in. Printing returns spooler id 123 (optionally blocking until released), and
   /// cancellation can succeed, be refused with a chosen outcome, or throw after the action to mimic a lost worker reply.
   /// </summary>
   internal sealed class SyntheticPrinter : IPrinterBackend, IPrinterJobControl
   {
      internal int Cancellations;
      internal bool BlockPrint { get; set; }
      internal string? CancellationOutcome { get; set; }
      internal bool LoseCancellationResult { get; set; }
      internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);



      /// <summary>Reports the single ready PDF queue.</summary>
      public IReadOnlyList<PrinterInfo> Discover() => [new("printer", "Synthetic printer", "Fixture", "None", "READY", ["PDF"])];



      /// <summary>Signals that printing started, optionally waits to be released, and returns the fixed spooler job id.</summary>
      public async Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
      {
         Entered.TrySetResult();
         if(BlockPrint)
         { await Release.Task.WaitAsync(cancellationToken); }
         return "123";
      }



      /// <summary>Checks it was asked to cancel exactly the seeded spooler job, counts the call, then fails or refuses as scripted or succeeds.</summary>
      public Task CancelAsync(string printerName, string jobId, string spoolerJobId, CancellationToken cancellationToken = default)
      {
         Assert.Equal("Synthetic printer", printerName);
         Assert.Equal("job", jobId);
         Assert.Equal("123", spoolerJobId);
         Interlocked.Increment(ref Cancellations);
         if(LoseCancellationResult)
         {
            throw new IOException("Synthetic isolated worker reply lost after action.");
         }
         if(CancellationOutcome is not null)
         { throw new SpoolCancellationException(CancellationOutcome, "Synthetic refusal."); }
         return Task.CompletedTask;
      }
   }



   /// <summary>Fake print server driven by the fixture's scripted state: it counts each kind of call and can fail polls and receipts on demand.</summary>
   private sealed class Handler(ControlFixture fixture) : HttpMessageHandler
   {
      /// <summary>Routes by URL suffix to the matching scripted response and counter; unrecognized paths get an accepted-true reply.</summary>
      protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         string path = request.RequestUri!.AbsolutePath;
         object result = new { accepted = true };
         if(path.EndsWith("control/poll", StringComparison.Ordinal))
         {
            Interlocked.Increment(ref fixture.Polls);
            if(fixture.PollStatus != HttpStatusCode.OK)
            { return new HttpResponseMessage(fixture.PollStatus); }
            if(fixture.PollBody is not null)
            {
               return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(fixture.PollBody, Encoding.UTF8, "application/json") };
            }
            result = new DeviceControlPoll(1, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), 1, fixture.Desired, fixture.Commands);
         }
         else if(path.EndsWith("/receipt", StringComparison.Ordinal))
         {
            fixture.Receipts.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if(fixture.ReceiptStatus != HttpStatusCode.OK)
            {
               return new HttpResponseMessage(fixture.ReceiptStatus);
            }
            if(fixture.ReceiptFailures-- > 0)
            { throw new HttpRequestException("Synthetic lost receipt response."); }
         }
         else if(path.EndsWith("/printers", StringComparison.Ordinal))
         { Interlocked.Increment(ref fixture.Inventories); }
         else if(path.EndsWith("/heartbeat", StringComparison.Ordinal))
         { Interlocked.Increment(ref fixture.Heartbeats); }
         else if(path.EndsWith("/claim", StringComparison.Ordinal))
         {
            Interlocked.Increment(ref fixture.Claims);
            result = new { job = fixture.OfferJob ? Job() : null };
            fixture.OfferJob = false;
         }
         else if(path.EndsWith("/content", StringComparison.Ordinal))
         { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ControlFixture.Content) }; }
         else if(path.EndsWith("/queued", StringComparison.Ordinal))
         { result = new { jobs = Array.Empty<PrintJob>() }; }
         return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result, PrintApiClient.JsonOptions), Encoding.UTF8, "application/json") };
      }
   }
}
