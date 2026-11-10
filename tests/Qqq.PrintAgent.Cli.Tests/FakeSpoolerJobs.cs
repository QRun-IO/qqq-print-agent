/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Runtime.InteropServices;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// A Windows spooler that exists only in memory, for the cancellation rules. It holds queues by name, each with jobs by
/// number and their document names, records every call it was sent with its arguments, and can be told to fail each call
/// the way the real one can. A failed call sets the thread's last error, as the native call does, so the rules read it the
/// same way. No print queue on this computer is opened.
/// </summary>
internal sealed class FakeSpoolerJobs : ISpoolerJobs
{
   private readonly Dictionary<IntPtr, string> _open = [];
   private int _handles;



   /// <summary>The queues, by name; each holds its jobs by spooler number with the document name Windows stored (null for none).</summary>
   internal Dictionary<string, Dictionary<uint, string?>> Queues { get; } = new(StringComparer.Ordinal);



   /// <summary>Every call sent, in order, written as the call and its arguments.</summary>
   internal List<string> Calls { get; } = [];



   /// <summary>The error a queue that does not exist is refused with (ERROR_INVALID_PRINTER_NAME).</summary>
   internal int UnknownQueueError { get; set; } = 1801;



   /// <summary>The error a job that is not in the queue is refused with; Windows answers 87 for an unknown job number.</summary>
   internal int MissingJobError { get; set; } = 87;



   /// <summary>When set, the size the sizing call reports instead of the size the job's details really need.</summary>
   internal uint? ReportedSize { get; set; }



   /// <summary>When set, reading a job's details fails with this error although the job exists.</summary>
   internal int? ReadError { get; set; }



   /// <summary>When set, a command to a job fails with this error and the job stays where it is.</summary>
   internal int? CommandError { get; set; }



   /// <summary>Queue handles that were opened and not yet closed.</summary>
   internal int OpenHandles => _open.Count;



   /// <summary>Adds a queue holding one job and returns this fake, so a test can set it up in one expression.</summary>
   internal FakeSpoolerJobs WithJob(string queue, uint job, string? document)
   {
      if(!Queues.TryGetValue(queue, out Dictionary<uint, string?>? jobs))
      {
         jobs = [];
         Queues[queue] = jobs;
      }
      jobs[job] = document;
      return this;
   }



   /// <summary>Opens a known queue and hands out a new handle; an unknown name fails with the configured error.</summary>
   public bool OpenPrinter(string name, out IntPtr printer)
   {
      Calls.Add($"OpenPrinter({name})");
      if(!Queues.ContainsKey(name))
      {
         printer = IntPtr.Zero;
         return Fail(UnknownQueueError);
      }
      printer = new IntPtr(0x100 + ++_handles);
      _open[printer] = name;
      return true;
   }



   /// <summary>
   /// Answers the sizing call the way Windows does: false with the size the details need, or false with a size of zero
   /// and the missing-job error when the job is not in the queue.
   /// </summary>
   public bool GetJobSize(IntPtr printer, uint job, out uint needed)
   {
      Calls.Add($"GetJobSize({Handle(printer)}, {job})");
      if(!Queues[_open[printer]].TryGetValue(job, out string? document))
      {
         needed = 0;
         return Fail(MissingJobError);
      }
      needed = ReportedSize ?? DetailsSize(document);
      return Fail(122);
   }



   /// <summary>Reports the stored document name of a job, or fails with the configured read error.</summary>
   public bool GetJobDocument(IntPtr printer, uint job, uint size, out string? document)
   {
      Calls.Add($"GetJobDocument({Handle(printer)}, {job}, {size})");
      document = null;
      if(ReadError is { } error)
      {
         return Fail(error);
      }
      document = Queues[_open[printer]][job];
      return true;
   }



   /// <summary>Carries out the delete command (5) by removing the job, or fails with the configured command error.</summary>
   public bool SetJob(IntPtr printer, uint job, uint command)
   {
      Calls.Add($"SetJob({Handle(printer)}, {job}, {command})");
      if(CommandError is { } error)
      {
         return Fail(error);
      }
      if(command == 5)
      {
         _ = Queues[_open[printer]].Remove(job);
      }
      return true;
   }



   /// <summary>Closes a handle this fake handed out; closing anything else is a test failure.</summary>
   public bool ClosePrinter(IntPtr printer)
   {
      Calls.Add($"ClosePrinter({Handle(printer)})");
      Assert.True(_open.Remove(printer), "A handle was closed that was not open.");
      return true;
   }



   /// <summary>The size of a job's level 1 details in this fake: a fixed part plus the document name in UTF-16 with its terminator.</summary>
   internal static uint DetailsSize(string? document) => (uint)(64 + 2 * ((document?.Length ?? 0) + 1));



   /// <summary>Writes a handle the way the call log shows it.</summary>
   private static string Handle(IntPtr handle) => "0x" + handle.ToInt64().ToString("x", CultureInfo.InvariantCulture);



   /// <summary>Leaves the error as the thread's last error, as a failed native call does, and reports the failure.</summary>
   private static bool Fail(int error)
   {
      Marshal.SetLastPInvokeError(error);
      return false;
   }
}
