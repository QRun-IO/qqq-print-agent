using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Qqq.PrintAgent.Windows;

/// <summary>Cancel only the exact QQQ document identified by the durable receipt.</summary>
public static class WindowsSpoolerJobs
{
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
         if(needed == 0 || needed > 1_048_576)
         { throw new InvalidOperationException("The job is no longer in the Windows queue. Inspect its output."); }
         IntPtr buffer = Marshal.AllocHGlobal((int)needed);
         try
         {
            if(!GetJob(printer, id, 1, buffer, needed, out _))
            { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            JobInfo1 info = Marshal.PtrToStructure<JobInfo1>(buffer);
            if(!string.Equals(Marshal.PtrToStringUni(info.Document), "QQQ " + jobId, StringComparison.Ordinal))
            {
               throw new InvalidOperationException("The spooler number now belongs to another document; cancellation was refused.");
            }
            const uint jobControlDelete = 5;
            if(!SetJob(printer, id, 0, IntPtr.Zero, jobControlDelete))
            { throw new Win32Exception(Marshal.GetLastWin32Error()); }
         }
         finally { Marshal.FreeHGlobal(buffer); }
      }
      finally { _ = ClosePrinter(printer); }
   }

   [StructLayout(LayoutKind.Sequential)]
   private struct JobInfo1
   {
      internal uint Id;
      internal IntPtr Printer, Machine, User, Document, DataType, StatusText;
      internal uint Status, Priority, Position, TotalPages, PagesPrinted;
      internal ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
   }
   [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenPrinter(string name, out IntPtr printer, IntPtr defaults);
   [DllImport("winspool.drv", EntryPoint = "GetJobW", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetJob(IntPtr printer, uint job, uint level, IntPtr buffer, uint size, out uint needed);
   [DllImport("winspool.drv", EntryPoint = "SetJobW", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetJob(IntPtr printer, uint job, uint level, IntPtr buffer, uint command);
   [DllImport("winspool.drv", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClosePrinter(IntPtr printer);
}
