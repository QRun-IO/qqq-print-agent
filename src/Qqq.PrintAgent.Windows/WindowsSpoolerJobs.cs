/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Qqq.PrintAgent.Windows;

/// <summary>Cancel only the exact QQQ document identified by the durable receipt.</summary>
public static class WindowsSpoolerJobs
{
   /// <summary>
   /// Cancels one spooler job by its number, but only after reading the job back and confirming its document name is
   /// "QQQ" plus this job's identifier. A number that has been reused by someone else's document is refused, a job that has
   /// already left the queue is reported as absent, and the answer size is bounded. Pages already printed cannot be recalled.
   /// </summary>
   public static void Cancel(string printerName, string jobId, string spoolerJobId)
   {
      if(!uint.TryParse(spoolerJobId, NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
      {
         throw new InvalidOperationException("This job has no valid Windows spooler receipt.");
      }
      if(!OpenPrinter(printerName, out IntPtr printer, IntPtr.Zero))
      { throw new Win32Exception(Marshal.GetLastWin32Error()); }
      try
      {
         _ = GetJob(printer, id, 1, IntPtr.Zero, 0, out uint needed);
         if(needed == 0)
         {
            int error = Marshal.GetLastWin32Error();
            if(error is 87 or 1168)
            {
               throw new SpoolCancellationException("ALREADY_ABSENT", "The job is no longer in the Windows queue. Pages already printed cannot be recalled.");
            }
            throw new Win32Exception(error);
         }
         if(needed > 1_048_576)
         { throw new InvalidOperationException("The Windows spooler response exceeded its bound."); }
         IntPtr buffer = Marshal.AllocHGlobal((int)needed);
         try
         {
            if(!GetJob(printer, id, 1, buffer, needed, out _))
            { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            JobInfo1 info = Marshal.PtrToStructure<JobInfo1>(buffer);
            if(!string.Equals(Marshal.PtrToStringUni(info.Document), "QQQ " + jobId, StringComparison.Ordinal))
            {
               throw new SpoolCancellationException("OWNERSHIP_MISMATCH", "The spooler number now belongs to another document; cancellation was refused.");
            }
            const uint jobControlDelete = 5;
            if(!SetJob(printer, id, 0, IntPtr.Zero, jobControlDelete))
            { throw new Win32Exception(Marshal.GetLastWin32Error()); }
         }
         finally { Marshal.FreeHGlobal(buffer); }
      }
      finally { _ = ClosePrinter(printer); }
   }



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
