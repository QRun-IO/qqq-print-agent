/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;


namespace Qqq.PrintAgent.Windows;

/// <summary>Bounds blocking printer drivers inside a disposable helper rather than the service process.</summary>
public sealed class IsolatedPrinterBackend(string helperExecutable) : IPrinterBackend, IPrinterJobControl
{
   internal TimeSpan? OperationTimeoutOverride { get; init; }
   internal Func<ProcessStartInfo, Process>? StartProcess { get; init; }



   /// <summary>
   /// Asks a disposable helper process to print the staged file at the given absolute path and returns the spooler receipt.
   /// Only the path travels to the helper, never the document bytes. The helper is ended, with its process tree, when it
   /// outlives the submission limit. A timeout or helper failure after submission began means the outcome is uncertain
   /// and is reported as such by the caller, never retried here.
   /// </summary>
   public async Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken)
   {
      PrinterWorkerHost.Response response = await InvokeAsync(new PrinterWorkerHost.Request("print-file", job, ContentPath: Path.GetFullPath(path)), submissionTimeout, cancellationToken).ConfigureAwait(false);
      return response.SpoolerJobId ?? throw new InvalidDataException("Printer worker returned no spooler receipt.");
   }



   /// <summary>
   /// Discovery remains synchronous for the backend contract but cannot block beyond 20 seconds. Its helper reads the settings of
   /// every queue, so one queue whose driver hangs uses up the whole limit and the discovery fails.
   /// </summary>
   public IReadOnlyList<PrinterInfo> Discover() => ReadPrinters(new PrinterWorkerHost.Request("discover"));



   /// <summary>Lists the queues without their media through a helper of its own, with the same 20 second limit and the same failures as discovery. That helper opens no queue and asks no driver; the spooler's own enumeration can still hold it until the limit, as a connection to a print server that does not answer does.</summary>
   public IReadOnlyList<PrinterInfo> ListWithoutMedia() => ReadPrinters(new PrinterWorkerHost.Request("list-without-media"));



   /// <summary>
   /// Looks up one queue with its media through a helper of its own, with the same 20 second limit and the same failures as
   /// discovery; that helper reads the settings of this queue only. Text that does not have the form of a queue identifier names
   /// no queue, so it reads as absent and no helper is started for it; the request therefore always carries 24 characters. The
   /// helper answers with no printer or with one, and an answer that holds more than one, one that was not asked for, or an
   /// entry that is no printer at all (the JSON word null), is refused.
   /// </summary>
   public PrinterInfo? FindWithMedia(string printerId)
   {
      if(!WindowsPrinters.IsQueueIdentifier(printerId))
      {
         return null;
      }
      IReadOnlyList<PrinterInfo> found = ReadPrinters(new PrinterWorkerHost.Request("find-with-media", PrinterId: printerId));
      if(found.Count > 1 || found.Any(printer => printer is null || !string.Equals(printer.PrinterId, printerId, StringComparison.Ordinal)))
      {
         throw new InvalidDataException("Printer worker returned a printer that was not asked for.");
      }
      return found.Count == 0 ? null : found[0];
   }



   /// <summary>Runs one of the three reads in a helper, waits for it for at most 20 seconds, and returns the printers it answered with; an answer that claims success and carries no list is refused.</summary>
   private IReadOnlyList<PrinterInfo> ReadPrinters(PrinterWorkerHost.Request request)
      => InvokeAsync(request, TimeSpan.FromSeconds(20), CancellationToken.None).GetAwaiter().GetResult().Printers
         ?? throw new InvalidDataException("Printer worker returned no inventory.");



   /// <summary>Cancels one spooler job through the helper process, with a 20 second limit; the helper confirms the job belongs to this QQQ job before it deletes anything.</summary>
   public async Task CancelAsync(string printerName, string jobId, string spoolerJobId, CancellationToken cancellationToken = default)
   {
      _ = await InvokeAsync(new PrinterWorkerHost.Request("cancel", PrinterName: printerName, JobId: jobId, SpoolerJobId: spoolerJobId), TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
   }



   /// <summary>
   /// Runs one native operation in a fresh helper process: the request goes in as one JSON line on standard input, the answer
   /// comes back on standard output within the size limit, and the helper is killed with its process tree if it outlives the
   /// limit. A timeout is reported as a timeout the operator must inspect, not as a clean failure.
   /// </summary>
   private async Task<PrinterWorkerHost.Response> InvokeAsync(PrinterWorkerHost.Request request, TimeSpan limit, CancellationToken token)
   {
      token.ThrowIfCancellationRequested();
      ProcessStartInfo start = new(Path.GetFullPath(helperExecutable))
      {
         UseShellExecute = false,
         CreateNoWindow = true,
         RedirectStandardInput = true,
         RedirectStandardOutput = true,
         RedirectStandardError = true,
      };
      start.ArgumentList.Add("--printer-worker");
      start.Environment[PrinterWorkerHost.ParentEnvironmentVariable] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
      using Process child = StartProcess?.Invoke(start) ?? Process.Start(start) ?? throw new InvalidOperationException("The isolated printer worker could not start.");
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
      timeout.CancelAfter(OperationTimeoutOverride ?? limit);
      Task<string> output = child.StandardOutput.ReadToEndAsync(timeout.Token);
      Task<string> error = child.StandardError.ReadToEndAsync(timeout.Token);
      try
      {
         await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, PrintApiClient.JsonOptions).AsMemory(), timeout.Token).ConfigureAwait(false);
         child.StandardInput.Close();
         await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
         string text = await output.ConfigureAwait(false);
         _ = await error.ConfigureAwait(false);
         if(text.Length > 1_048_576)
         {
            throw new InvalidDataException("Printer worker response exceeded its limit.");
         }
         PrinterWorkerHost.Response response = JsonSerializer.Deserialize<PrinterWorkerHost.Response>(text, PrintApiClient.JsonOptions) ?? throw new InvalidDataException("Printer worker returned no response.");
         if(child.ExitCode != 0 || !response.Success)
         {
            ThrowWorkerFailure(response);
         }
         return response;
      }
      catch(OperationCanceledException exception) when(!token.IsCancellationRequested)
      {
         throw new TimeoutException("The printer operation timed out. Inspect the printer and job outcome before retrying.", exception);
      }
      finally
      {
         if(!child.HasExited)
         {
            child.Kill(entireProcessTree: true);
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            try
            {
               await child.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
            }
            catch(OperationCanceledException)
            {
               // The submission remains uncertain even if a blocked kernel driver delays termination.
            }
         }
      }
   }



   /// <summary>Turns a failed helper response into the right exception: a proven media mismatch, a refused spool cancellation, or a generic failure, so callers can tell a known refusal from an uncertain outcome.</summary>
   private static void ThrowWorkerFailure(PrinterWorkerHost.Response response)
   {
      if(string.Equals(response.ErrorCode, "MEDIA_PROFILE_MISMATCH", StringComparison.Ordinal))
      { throw new PrinterMediaMismatchException(response.Error ?? "The selected printer default changed before submission."); }
      if(response.ErrorCode is "ALREADY_ABSENT" or "OWNERSHIP_MISMATCH")
      { throw new SpoolCancellationException(response.ErrorCode, response.Error ?? "Verified spool cancellation was refused."); }
      throw new IOException(response.Error ?? "The printer worker stopped unexpectedly. Inspect the printer before retrying.");
   }
}
