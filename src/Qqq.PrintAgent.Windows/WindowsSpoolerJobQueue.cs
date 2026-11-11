/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The real Windows spooler behind <see cref="ISpoolerJobs"/>: each member makes one call into winspool.drv and reports
/// what it returned. It decides nothing about jobs.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Calls OpenPrinter, GetJob, SetJob and ClosePrinter in winspool.drv. A test cannot run these without opening a real Windows print queue on this computer and reading, or deleting, one of its jobs. No rule lives here: which job is read, what counts as this application's document, when a job is reported as already gone, the bound on the answer and whether the job is deleted are decided in WindowsSpoolerJobs, which is tested with a fake of ISpoolerJobs. Fixed here, because each is the shape of one call and not a rule: OpenPrinter passes no defaults structure; GetJob is always asked at level 1, and its sizing call passes no buffer and size 0; SetJob is called at level 0 with no job details, which sends the command alone. The one branch here reads the document name only when Windows filled the buffer, because the pointers in an unfilled buffer are not valid. After that buffer is freed, the error GetJob left is put back as the thread's last error, so the caller reads the error of GetJob and not of the cleanup.")]
internal sealed class WindowsSpoolerJobQueue : ISpoolerJobs
{
   /// <summary>Opens the queue with no defaults structure, so the handle has the caller's default access.</summary>
   public bool OpenPrinter(string name, out IntPtr printer) => OpenPrinter(name, out printer, IntPtr.Zero);



   /// <summary>Calls GetJob at level 1 with no buffer, which only reports the size the details need.</summary>
   public bool GetJobSize(IntPtr printer, uint job, out uint needed) => GetJob(printer, job, 1, IntPtr.Zero, 0, out needed);



   /// <summary>
   /// Calls GetJob at level 1 into an unmanaged buffer of the given size and copies the document name out of it before
   /// the buffer is freed. A failed call leaves the buffer unfilled, so nothing is read from it. The error GetJob left is
   /// put back after the buffer is freed, so the caller reads the error of GetJob and not of the cleanup.
   /// </summary>
   public bool GetJobDocument(IntPtr printer, uint job, uint size, out string? document)
   {
      int error = 0;
      IntPtr buffer = Marshal.AllocHGlobal((int)size);
      try
      {
         bool read = GetJob(printer, job, 1, buffer, size, out _);
         error = Marshal.GetLastPInvokeError();
         document = read ? Marshal.PtrToStringUni(Marshal.PtrToStructure<JobInfo1>(buffer).Document) : null;
         return read;
      }
      finally
      {
         Marshal.FreeHGlobal(buffer);
         Marshal.SetLastPInvokeError(error);
      }
   }



   /// <summary>Calls SetJob at level 0 with no job details, which is how a command alone is sent.</summary>
   public bool SetJob(IntPtr printer, uint job, uint command) => SetJob(printer, job, 0, IntPtr.Zero, command);



   /// <summary>Closes the queue handle.</summary>
   bool ISpoolerJobs.ClosePrinter(IntPtr printer) => ClosePrinter(printer);



   /// <summary>JOB_INFO_1 as returned by GetJob level 1; only the document name is used, to prove ownership. The layout is marshaled and must not change.</summary>
   [StructLayout(LayoutKind.Sequential)]
   private struct JobInfo1
   {
      internal uint Id;
      internal IntPtr Printer, Machine, User, Document, DataType, StatusText;
      internal uint Status, Priority, Position, TotalPages, PagesPrinted;
      internal ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
   }



   /// <summary>Opens a queue by name so one of its jobs can be read and cancelled; the handle must be closed.</summary>
   [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenPrinter(string name, out IntPtr printer, IntPtr defaults);



   /// <summary>Reads one job's details; called first with no buffer to learn the size, then again to fill it.</summary>
   [DllImport("winspool.drv", EntryPoint = "GetJobW", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetJob(IntPtr printer, uint job, uint level, IntPtr buffer, uint size, out uint needed);



   /// <summary>Sends a control command, here delete, to one spooler job.</summary>
   [DllImport("winspool.drv", EntryPoint = "SetJobW", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetJob(IntPtr printer, uint job, uint level, IntPtr buffer, uint command);



   /// <summary>Releases a queue handle opened by OpenPrinter.</summary>
   [DllImport("winspool.drv", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClosePrinter(IntPtr printer);
}
