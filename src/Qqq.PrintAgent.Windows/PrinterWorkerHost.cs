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
   internal sealed record Request(string Operation, PrintJob? Job = null, byte[]? Content = null, string? PrinterName = null, string? JobId = null, string? SpoolerJobId = null);
   internal sealed record Response(bool Success, IReadOnlyList<PrinterInfo>? Printers = null, string? SpoolerJobId = null, string? Error = null);

   /// <summary>Run before ordinary service or CLI hosting when invoked with --printer-worker.</summary>
   public static Task<int> RunAsync() => RunAsync(new WindowsPrinters());

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
            default:
               throw new InvalidDataException("Unknown printer worker operation.");
         }
         await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, PrintApiClient.JsonOptions)).ConfigureAwait(false);
         return 0;
      }
      catch(Exception exception)
      {
         await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new Response(false, Error: AgentActivityLog.Redact(exception.Message)), PrintApiClient.JsonOptions)).ConfigureAwait(false);
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
