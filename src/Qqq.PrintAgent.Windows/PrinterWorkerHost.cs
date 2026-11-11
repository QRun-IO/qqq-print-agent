/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;


namespace Qqq.PrintAgent.Windows;

/// <summary>One native printer operation in a disposable process. A document reaches it only as the path of a verified staged file, never as content.</summary>
public static class PrinterWorkerHost
{
   internal const string ParentEnvironmentVariable = "QQQ_PRINT_WORKER_PARENT";



   /// <summary>One operation sent to the helper as a single JSON line: discover, print a staged file, or cancel a spool job, with only the fields that operation needs. A staged file travels by path, never by content.</summary>
   internal sealed record Request(string Operation, PrintJob? Job = null, string? PrinterName = null, string? JobId = null, string? SpoolerJobId = null, string? ContentPath = null);



   /// <summary>The helper's single-line answer: success with the printers or spooler receipt, or a failure with a redacted message and, for known refusals, a code the parent maps back to a specific exception.</summary>
   internal sealed record Response(bool Success, IReadOnlyList<PrinterInfo>? Printers = null, string? SpoolerJobId = null, string? Error = null, string? ErrorCode = null);



   /// <summary>
   /// Runs one operation against the Windows printers and spooler, reading the request from standard input and the parent's
   /// process id from the environment. The service's executable calls this, before it hosts anything, when the service
   /// started it with --printer-worker as its print helper. The Windows adapters are built here, not held in fields, so
   /// nothing exists until a helper is actually run.
   /// </summary>
   public static Task<int> RunAsync()
      => RunAsync(new WindowsPrinters(), new WindowsSpoolerJobQueue(), new ParentProcessWatch(), Environment.GetEnvironmentVariable(ParentEnvironmentVariable), Console.In, Console.Out);



   /// <summary>
   /// Runs exactly one operation read from the input and writes one JSON answer to the output, with exit code 0 for
   /// success and 1 for failure. It refuses to start without a parent process id, has that parent watched from before the
   /// request is read until the operation is done, bounds the request size, and redacts error text before it leaves the
   /// process. The printers, the spooler's job calls, the watch and the two streams are given, so tests run it without a
   /// printer, a second process or the console.
   /// </summary>
   internal static async Task<int> RunAsync(IPrinterBackend printers, ISpoolerJobs spoolerJobs, IParentWatch parentWatch, string? parent, TextReader input, TextWriter output)
   {
      using CancellationTokenSource lifetime = new();
      Task? watch = null;
      try
      {
         if(!int.TryParse(parent, NumberStyles.None, CultureInfo.InvariantCulture, out int parentId))
         {
            throw new InvalidOperationException("Printer workers must be launched by the print connector.");
         }
         watch = parentWatch.WatchAsync(parentId, lifetime.Token);
         string? line = await input.ReadLineAsync().ConfigureAwait(false);
         if(line is null || line.Length > 30_000_000)
         {
            throw new InvalidDataException("Invalid printer worker request size.");
         }
         Request request = JsonSerializer.Deserialize<Request>(line, PrintApiClient.JsonOptions) ?? throw new InvalidDataException("Missing printer worker request.");
         Response response = await ExecuteAsync(request, printers, spoolerJobs, lifetime.Token).ConfigureAwait(false);
         await output.WriteLineAsync(JsonSerializer.Serialize(response, PrintApiClient.JsonOptions)).ConfigureAwait(false);
         return 0;
      }
      catch(Exception exception)
      {
         await output.WriteLineAsync(JsonSerializer.Serialize(new Response(false, Error: AgentActivityLog.Redact(exception.Message), ErrorCode: exception is PrinterMediaMismatchException ? "MEDIA_PROFILE_MISMATCH" : (exception as SpoolCancellationException)?.Outcome), PrintApiClient.JsonOptions)).ConfigureAwait(false);
         return 1;
      }
      finally
      {
         await lifetime.CancelAsync().ConfigureAwait(false);
         if(watch is not null)
         {
            await watch.ConfigureAwait(false);
         }
      }
   }



   /// <summary>
   /// Carries out the one operation a request names and builds its answer: the inventory for discover, nothing but success
   /// for cancel (which reads the job back and proves it is ours before deleting it), and the spooler receipt for
   /// print-file, which needs both a job and the path of its verified file. Any other operation is refused.
   /// </summary>
   private static async Task<Response> ExecuteAsync(Request request, IPrinterBackend printers, ISpoolerJobs spoolerJobs, CancellationToken token)
   {
      switch(request.Operation)
      {
         case "discover":
            return new Response(true, printers.Discover());
         case "cancel":
            WindowsSpoolerJobs.Cancel(spoolerJobs, request.PrinterName ?? "", request.JobId ?? "", request.SpoolerJobId ?? "");
            return new Response(true);
         case "print-file":
            if(request.Job is null || request.ContentPath is null)
            {
               throw new InvalidDataException("The print worker requires a verified document file.");
            }
            ////////////////////////////////////////////////////////////////////////
            // no limit is passed on: the service that started this process holds //
            // the submission limit and ends the process when it passes           //
            ////////////////////////////////////////////////////////////////////////
            return new Response(true, SpoolerJobId: await printers.PrintFileAsync(request.Job, request.ContentPath, Timeout.InfiniteTimeSpan, token).ConfigureAwait(false));
         default:
            throw new InvalidDataException("Unknown printer worker operation.");
      }
   }
}
