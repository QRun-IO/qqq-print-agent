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
   /// <summary>
   /// Selects the fixture behavior from the arguments. With --launch-parent it starts a hanging child worker whose parent id is this
   /// process, reports the child's id, and exits, so a test can check the child dies with its parent. Otherwise it runs the real
   /// worker loop against a fake backend that can hang (--hang) or report a media change (--media-drift).
   /// </summary>
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
      return await PrinterWorkerHost.RunAsync(new FakePrinters(args.Contains("--hang", StringComparer.Ordinal), args.Contains("--media-drift", StringComparer.Ordinal))).ConfigureAwait(false);
   }



   /// <summary>The fake printer backend the worker loop talks to: one synthetic queue, with optional hanging or media-drift behavior, and no ability to print anything.</summary>
   private sealed class FakePrinters(bool hang, bool mediaDrift) : IFilePrinterBackend
   {
      /// <summary>Returns the single synthetic queue; in hang mode it first announces READY and then blocks forever, simulating a native call that never returns.</summary>
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



      /// <summary>Always throws: this fixture must never print.</summary>
      public Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
         => throw new InvalidOperationException("This fixture must never print.");



      /// <summary>Reports a media change when asked to, blocks forever in hang mode, and otherwise throws; it never opens the document or prints.</summary>
      public Task<string> PrintFileAsync(PrintJob job, string path, CancellationToken cancellationToken)
      {
         if(mediaDrift)
         { throw new PrinterMediaMismatchException("The fixture changed its default before submission."); }
         if(hang)
         { Thread.Sleep(Timeout.Infinite); }
         throw new InvalidOperationException("This fixture never opens documents or prints.");
      }
   }
}
