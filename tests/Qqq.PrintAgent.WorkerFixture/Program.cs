/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Globalization;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;


namespace Qqq.PrintAgent.WorkerFixture;

/// <summary>Test-only worker; never constructs a native printer backend or submits output.</summary>
public static class Program
{
   public static async Task<int> Main(string[] args)
   {
      if(args.Contains("--launch-parent", StringComparer.Ordinal))
      {
         ProcessStartInfo start = new(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
         start.ArgumentList.Add("--hang");
         start.Environment["QQQ_PRINT_WORKER_PARENT"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
         using Process child = Process.Start(start) ?? throw new InvalidOperationException("Fixture worker failed to start.");
         await child.StandardInput.WriteLineAsync("{\"operation\":\"discover\"}").ConfigureAwait(false);
         await child.StandardInput.FlushAsync().ConfigureAwait(false);
         if(!string.Equals(await child.StandardOutput.ReadLineAsync().ConfigureAwait(false), "READY", StringComparison.Ordinal))
         {
            return 2;
         }
         Console.WriteLine(child.Id.ToString(CultureInfo.InvariantCulture));
         return 0;
      }
      return await PrinterWorkerHost.RunAsync(new FakePrinters(args.Contains("--hang", StringComparer.Ordinal))).ConfigureAwait(false);
   }

   private sealed class FakePrinters(bool hang) : IPrinterBackend
   {
      public IReadOnlyList<PrinterInfo> Discover()
      {
         if(hang)
         {
            Console.WriteLine("READY");
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
         }
         return [new PrinterInfo("fixture", "Synthetic queue", "Fixture", "None", "READY", ["PDF"])];
      }

      public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
         => throw new InvalidOperationException("This fixture must never print.");
   }
}
