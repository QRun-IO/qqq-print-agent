using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

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

   internal ControlFixture()
   {
      using AgentStore store = new(DirectoryPath);
      store.SaveConfiguration(new AgentConfiguration("https://synthetic.invalid", "device", "synthetic-token"));
      store.SetExposure("printer", true, Printer.Discover());
   }

   internal AgentRuntime Runtime()
   {
      string pipe = "qqq-control-test-" + Guid.NewGuid().ToString("N");
      return new AgentRuntime(DirectoryPath, _ => { }, Printer, config => new PrintApiClient(config.ServerUrl, config.DeviceToken, new Handler(this)), pipe,
         () => new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
      { ControlIntervalOverride = TimeSpan.FromMilliseconds(30), HeartbeatInterval = TimeSpan.FromMilliseconds(30) };
   }

   internal static DeviceControlCommand Command(string action = "CANCEL_SPOOL", string id = "command", bool expired = false) => new(id, "request-" + id, action,
      DateTimeOffset.UtcNow.AddMinutes(expired ? -20 : -1).ToString("O", CultureInfo.InvariantCulture),
      DateTimeOffset.UtcNow.AddMinutes(expired ? -10 : 10).ToString("O", CultureInfo.InvariantCulture), string.Equals(action, "CANCEL_SPOOL", StringComparison.Ordinal) ? "job" : null);

   internal void SeedSpooledJob()
   {
      using AgentStore store = new(DirectoryPath);
      store.TrackJob(Job(), "Synthetic printer");
      PrintEvent receipt = new("spooled", "SPOOLED", SpoolerJobId: "123");
      store.Record("job", receipt);
      store.Acknowledge(receipt.EventId);
   }

   private static PrintJob Job() => new("job", "printer", "Synthetic", "PDF", Convert.ToHexString(SHA256.HashData(Content)), Content.Length,
      new PrintOptions(), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), "CLAIMED");

   public void Dispose()
   {
      Printer.Release.TrySetResult();
      Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
      Directory.Delete(DirectoryPath, recursive: true);
   }

   internal sealed class SyntheticPrinter : IPrinterBackend, IPrinterJobControl
   {
      internal int Cancellations;
      internal bool BlockPrint { get; set; }
      internal string? CancellationOutcome { get; set; }
      internal bool LoseCancellationResult { get; set; }
      internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
      public IReadOnlyList<PrinterInfo> Discover() => [new("printer", "Synthetic printer", "Fixture", "None", "READY", ["PDF"])];
      public async Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
      {
         Entered.TrySetResult();
         if(BlockPrint)
         { await Release.Task.WaitAsync(cancellationToken); }
         return "123";
      }
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

   private sealed class Handler(ControlFixture fixture) : HttpMessageHandler
   {
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
