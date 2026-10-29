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

   /// <summary>Discovery remains synchronous for the backend contract but cannot block beyond 20 seconds.</summary>
   public IReadOnlyList<PrinterInfo> Discover()
      => InvokeAsync(new PrinterWorkerHost.Request("discover"), TimeSpan.FromSeconds(20), CancellationToken.None).GetAwaiter().GetResult().Printers
         ?? throw new InvalidDataException("Printer worker returned no inventory.");

   /// <summary>Once invoked by the journaled worker, timeout or process failure means uncertain physical outcome.</summary>
   public async Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
   {
      PrinterWorkerHost.Response response = await InvokeAsync(new PrinterWorkerHost.Request("print", job, content), TimeSpan.FromSeconds(120), cancellationToken).ConfigureAwait(false);
      return response.SpoolerJobId ?? throw new InvalidDataException("Printer worker returned no spooler receipt.");
   }

   /// <inheritdoc/>
   public async Task CancelAsync(string printerName, string jobId, string spoolerJobId, CancellationToken cancellationToken = default)
   {
      _ = await InvokeAsync(new PrinterWorkerHost.Request("cancel", PrinterName: printerName, JobId: jobId, SpoolerJobId: spoolerJobId), TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
   }

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
            throw new IOException(response.Error ?? "The printer worker stopped unexpectedly. Inspect the printer before retrying.");
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
}
