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
   /// process, reports the child's id, and exits, so a test can check the child dies with its parent. With --stand-in first it is
   /// the plain child program of <see cref="StandIn"/> and never touches the worker. With --respond or --flood it stands in for a
   /// helper that answers something of its own (see <see cref="AnswerAsync"/>). Otherwise it runs the real worker loop, with the
   /// real parent watch, against a fake backend that can hang (--hang) or report a media change (--media-drift) and a fake spooler
   /// that holds one job.
   /// </summary>
   public static async Task<int> Main(string[] args)
   {
      if(args.Length > 1 && string.Equals(args[0], StandIn.Flag, StringComparison.Ordinal))
      {
         return StandIn.Run(args);
      }
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
      if(args.Contains("--respond", StringComparer.Ordinal) || args.Contains("--flood", StringComparer.Ordinal))
      {
         return await AnswerAsync(args).ConfigureAwait(false);
      }
      return await PrinterWorkerHost.RunAsync(new FakePrinters(args.Contains("--hang", StringComparer.Ordinal), args.Contains("--media-drift", StringComparer.Ordinal)), new FakeJobs(),
         new ParentProcessWatch(), Environment.GetEnvironmentVariable("QQQ_PRINT_WORKER_PARENT"), Console.In, Console.Out).ConfigureAwait(false);
   }



   /// <summary>
   /// Stands in for a helper whose answer a test dictates, so the service side can be shown every answer a helper could give.
   /// It reads the one request line first, as a helper does. With --record FILE it writes its arguments, the parent id it was
   /// given and the request to that file. Then it prints the text after --respond, or with --flood more than the service
   /// accepts, and exits with the code after --exit-code (0 when absent).
   /// </summary>
   private static async Task<int> AnswerAsync(string[] args)
   {
      string? request = await Console.In.ReadLineAsync().ConfigureAwait(false);
      if(Value(args, "--record") is { } record)
      {
         await File.WriteAllLinesAsync(record, [string.Join(' ', args.TakeWhile(argument => !string.Equals(argument, "--respond", StringComparison.Ordinal))), Environment.GetEnvironmentVariable("QQQ_PRINT_WORKER_PARENT") ?? "", request ?? ""]).ConfigureAwait(false);
      }
      await Console.Out.WriteLineAsync(args.Contains("--flood", StringComparer.Ordinal) ? new string('x', 1_048_577) : Value(args, "--respond")).ConfigureAwait(false);
      return int.Parse(Value(args, "--exit-code") ?? "0", CultureInfo.InvariantCulture);
   }



   /// <summary>The argument that follows the named one, or null when the name is absent or last.</summary>
   private static string? Value(string[] args, string name)
   {
      int index = Array.IndexOf(args, name);
      return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
   }



   /// <summary>The fake printer backend the worker loop talks to: one synthetic queue, with optional hanging or media-drift behavior, and no ability to print anything.</summary>
   private sealed class FakePrinters(bool hang, bool mediaDrift) : IPrinterBackend
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



      /// <summary>Reports a media change when asked to, blocks forever in hang mode, and otherwise throws; it never opens the document or prints.</summary>
      public Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken)
      {
         if(mediaDrift)
         { throw new PrinterMediaMismatchException("The fixture changed its default before submission."); }
         if(hang)
         { Thread.Sleep(Timeout.Infinite); }
         throw new InvalidOperationException("This fixture never opens documents or prints.");
      }
   }



   /// <summary>
   /// The fake spooler the worker loop cancels in: one queue, "Synthetic queue", holding job 7 with the document name
   /// "QQQ fixture-job". The real cancellation rules run against it, so a test gets a deletion, an ownership refusal or an
   /// absent job by what it asks for. No Windows print queue is opened.
   /// </summary>
   private sealed class FakeJobs : ISpoolerJobs
   {
      /// <summary>
      /// 0x0BADC0DE, the marker every call here leaves as the thread's last error before it does anything else. The native
      /// imports these calls stand for are declared with SetLastError and replace the last error each time, so a rule that
      /// reads an error after a later call gets the marker and not the 87 or 122 the sizing call set.
      /// </summary>
      private const int StaleError = 0x0BAD_C0DE;



      /// <summary>Opens the one synthetic queue; any other name fails.</summary>
      public bool OpenPrinter(string name, out IntPtr printer)
      {
         System.Runtime.InteropServices.Marshal.SetLastPInvokeError(StaleError);
         printer = new IntPtr(1);
         return string.Equals(name, "Synthetic queue", StringComparison.Ordinal);
      }



      /// <summary>Reports a size for job 7 and no size, with error 87, for any other job, as Windows does for an unknown job number.</summary>
      public bool GetJobSize(IntPtr printer, uint job, out uint needed)
      {
         needed = job == 7 ? 128u : 0u;
         System.Runtime.InteropServices.Marshal.SetLastPInvokeError(job == 7 ? 122 : 87);
         return false;
      }



      /// <summary>Reports the document name of the one job.</summary>
      public bool GetJobDocument(IntPtr printer, uint job, uint size, out string? document)
      {
         System.Runtime.InteropServices.Marshal.SetLastPInvokeError(StaleError);
         document = "QQQ fixture-job";
         return true;
      }



      /// <summary>Accepts the command; there is nothing to delete.</summary>
      public bool SetJob(IntPtr printer, uint job, uint command)
      {
         System.Runtime.InteropServices.Marshal.SetLastPInvokeError(StaleError);
         return true;
      }



      /// <summary>Accepts the close.</summary>
      public bool ClosePrinter(IntPtr printer)
      {
         System.Runtime.InteropServices.Marshal.SetLastPInvokeError(StaleError);
         return true;
      }
   }
}
