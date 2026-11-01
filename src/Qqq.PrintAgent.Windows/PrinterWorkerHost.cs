/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;


namespace Qqq.PrintAgent.Windows;

/// <summary>One native printer operation in a disposable process, with documents confined to anonymous pipes and memory.</summary>
public static class PrinterWorkerHost
{
   internal const string ParentEnvironmentVariable = "QQQ_PRINT_WORKER_PARENT";



   /// <summary>One operation sent to the helper as a single JSON line: discover, print, print a staged file, or cancel a spool job, with only the fields that operation needs. A staged file travels by path, never by content.</summary>
   internal sealed record Request(string Operation, PrintJob? Job = null, byte[]? Content = null, string? PrinterName = null, string? JobId = null, string? SpoolerJobId = null, string? ContentPath = null);



   /// <summary>The helper's single-line answer: success with the printers or spooler receipt, or a failure with a redacted message and, for known refusals, a code the parent maps back to a specific exception.</summary>
   internal sealed record Response(bool Success, IReadOnlyList<PrinterInfo>? Printers = null, string? SpoolerJobId = null, string? Error = null, string? ErrorCode = null);



   /// <summary>Run before ordinary service or CLI hosting when invoked with --printer-worker.</summary>
   public static Task<int> RunAsync() => RunAsync(new WindowsPrinters());



   /// <summary>
   /// Runs exactly one operation read from standard input and writes one JSON answer to standard output, with exit code 0 for
   /// success and 1 for failure. It refuses to start without a parent process id, ends itself if that parent exits, bounds the
   /// request size, and redacts error text before it leaves the process. A backend is injected so tests can run it without a printer.
   /// </summary>
   internal static async Task<int> RunAsync(IPrinterBackend printers)
   {
      using CancellationTokenSource lifetime = new();
      Task? parentWatch = null;
      try
      {
         if(!int.TryParse(Environment.GetEnvironmentVariable(ParentEnvironmentVariable), NumberStyles.None, CultureInfo.InvariantCulture, out int parentId))
         {
            throw new InvalidOperationException("Printer workers must be launched by the print connector.");
         }
         parentWatch = WatchParentAsync(parentId, lifetime.Token);
         string? input = await Console.In.ReadLineAsync().ConfigureAwait(false);
         if(input is null || input.Length > 30_000_000)
         {
            throw new InvalidDataException("Invalid printer worker request size.");
         }
         Request request = JsonSerializer.Deserialize<Request>(input, PrintApiClient.JsonOptions) ?? throw new InvalidDataException("Missing printer worker request.");
         Response response;
         switch(request.Operation)
         {
            case "discover":
               response = new Response(true, printers.Discover());
               break;
            case "print":
               if(request.Job is null || request.Content is not { Length: > 0 and <= 20_971_520 })
               {
                  throw new InvalidDataException("The print worker requires a job and bounded document.");
               }
               response = new Response(true, SpoolerJobId: await printers.PrintAsync(request.Job, request.Content, lifetime.Token).ConfigureAwait(false));
               break;
            case "cancel":
               WindowsSpoolerJobs.Cancel(request.PrinterName ?? "", request.JobId ?? "", request.SpoolerJobId ?? "");
               response = new Response(true);
               break;
            case "print-file":
               if(request.Job is null || request.ContentPath is null || printers is not IFilePrinterBackend files)
               {
                  throw new InvalidDataException("The print worker requires a verified document file.");
               }
               response = new Response(true, SpoolerJobId: await files.PrintFileAsync(request.Job, request.ContentPath, lifetime.Token).ConfigureAwait(false));
               break;
            default:
               throw new InvalidDataException("Unknown printer worker operation.");
         }
         await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, PrintApiClient.JsonOptions)).ConfigureAwait(false);
         return 0;
      }
      catch(Exception exception)
      {
         await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new Response(false, Error: AgentActivityLog.Redact(exception.Message), ErrorCode: exception is PrinterMediaMismatchException ? "MEDIA_PROFILE_MISMATCH" : (exception as SpoolCancellationException)?.Outcome), PrintApiClient.JsonOptions)).ConfigureAwait(false);
         return 1;
      }
      finally
      {
         await lifetime.CancelAsync().ConfigureAwait(false);
         if(parentWatch is not null)
         {
            await parentWatch.ConfigureAwait(false);
         }
      }
   }



   /// <summary>Waits for the parent service to exit and then terminates this helper immediately, so a blocked driver call can never leave an orphan process running after the service is gone.</summary>
   private static async Task WatchParentAsync(int parentId, CancellationToken token)
   {
      try
      {
         using var parent = Process.GetProcessById(parentId);
         await parent.WaitForExitAsync(token).ConfigureAwait(false);
         Environment.Exit(2);
      }
      catch(OperationCanceledException) when(token.IsCancellationRequested)
      {
         // The requested operation completed normally.
      }
      catch(ArgumentException)
      {
         Environment.Exit(2);
      }
   }
}
