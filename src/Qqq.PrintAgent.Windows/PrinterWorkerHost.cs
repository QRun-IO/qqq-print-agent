/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;


namespace Qqq.PrintAgent.Windows;

/// <summary>One native printer operation in a disposable process. A document reaches it only as the path of a verified staged file, never as content.</summary>
public static class PrinterWorkerHost
{
   /// <summary>
   /// The environment variable in which the service hands a helper the service's own process id. The helper refuses to run
   /// without it and has that process watched, so that it ends when the service is gone.
   /// </summary>
   internal const string ParentEnvironmentVariable = "QQQ_PRINT_WORKER_PARENT";



   /// <summary>
   /// The most characters a request may have. A request is one small JSON object (an operation, a job's description and the
   /// path of its staged file), a few kilobytes at most, because documents travel as files; 1 MiB is the bound the service
   /// puts on the helper's answer, so the two directions match.
   /// </summary>
   internal const int MaximumRequestCharacters = 1_048_576;



   /// <summary>
   /// One operation sent to the helper as a single JSON line: one of the three reads (discover, list without media, find one
   /// queue with its media), print a staged file, or cancel a spool job, with only the fields that operation needs. A staged
   /// file travels by path, never by content, and the one queue to find by its identifier, never by its name.
   /// </summary>
   internal sealed record Request(string Operation, PrintJob? Job = null, string? PrinterName = null, string? JobId = null, string? SpoolerJobId = null, string? ContentPath = null, string? PrinterId = null);



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
         Request request = JsonSerializer.Deserialize<Request>(await ReadRequestAsync(input).ConfigureAwait(false), PrintApiClient.JsonOptions) ?? throw new InvalidDataException("Missing printer worker request.");
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
   /// Reads the one request line a piece at a time and stops with an error as soon as more characters than a request may
   /// have arrived without a line end, so an oversized request is refused while it is read and never held in memory whole.
   /// Input that ends before any character arrives is refused the same way. A line ends at a carriage return or a line
   /// feed; nothing after it is used, because a helper carries out exactly one request.
   /// </summary>
   private static async Task<string> ReadRequestAsync(TextReader input)
   {
      StringBuilder line = new();
      char[] piece = new char[4096];
      bool any = false;
      while(true)
      {
         int read = await input.ReadAsync(piece, 0, piece.Length).ConfigureAwait(false);
         if(read == 0)
         {
            return any ? line.ToString() : throw new InvalidDataException("Invalid printer worker request size.");
         }
         any = true;
         int end = piece.AsSpan(0, read).IndexOfAny('\r', '\n');
         int count = end < 0 ? read : end;
         if(line.Length + count > MaximumRequestCharacters)
         {
            throw new InvalidDataException("Invalid printer worker request size.");
         }
         line.Append(piece, 0, count);
         if(end >= 0)
         {
            return line.ToString();
         }
      }
   }



   /// <summary>
   /// Carries out the one operation a request names and builds its answer: the inventory for discover, the same queues
   /// without their media for list-without-media, no printer or one for find-with-media, nothing but success for cancel
   /// (which reads the job back and proves it is ours before deleting it), and the spooler receipt for print-file, which
   /// needs both a job and the path of its verified file. Find-with-media is refused, before the printers are asked
   /// anything, unless it names its queue by text that has the form of a queue identifier. Any other operation is refused.
   /// </summary>
   private static async Task<Response> ExecuteAsync(Request request, IPrinterBackend printers, ISpoolerJobs spoolerJobs, CancellationToken token)
   {
      switch(request.Operation)
      {
         case "discover":
            return new Response(true, printers.Discover());
         case "list-without-media":
            return new Response(true, printers.ListWithoutMedia());
         case "find-with-media":
            if(!WindowsPrinters.IsQueueIdentifier(request.PrinterId))
            {
               throw new InvalidDataException("The print worker requires the identifier of the queue to find.");
            }
            return new Response(true, printers.FindWithMedia(request.PrinterId) is { } found ? [found] : []);
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
