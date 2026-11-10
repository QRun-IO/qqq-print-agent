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
   /// <summary>The spooler command that deletes a job (JOB_CONTROL_DELETE).</summary>
   private const uint JobControlDelete = 5;



   /// <summary>
   /// Cancels one spooler job by its number in the real Windows spooler, under the rules of
   /// <see cref="Cancel(ISpoolerJobs, string, string, string)"/>. The spooler is built here, not held in a field, so
   /// nothing is created until a cancellation is asked for.
   /// </summary>
   public static void Cancel(string printerName, string jobId, string spoolerJobId) => Cancel(new WindowsSpoolerJobQueue(), printerName, jobId, spoolerJobId);



   /// <summary>
   /// Cancels one spooler job by its number, but only after reading the job back and confirming its document name is
   /// "QQQ" plus this job's identifier. A number that has been reused by someone else's document is refused, a job that has
   /// already left the queue is reported as absent, and the answer size is bounded. Pages already printed cannot be recalled.
   /// The queue handle is closed on every path once it was opened.
   /// </summary>
   internal static void Cancel(ISpoolerJobs spooler, string printerName, string jobId, string spoolerJobId)
   {
      if(!uint.TryParse(spoolerJobId, NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
      {
         throw new InvalidOperationException("This job has no valid Windows spooler receipt.");
      }
      if(!spooler.OpenPrinter(printerName, out IntPtr printer))
      { throw new Win32Exception(Marshal.GetLastWin32Error()); }
      try
      {
         _ = spooler.GetJobSize(printer, id, out uint needed);
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
         if(!spooler.GetJobDocument(printer, id, needed, out string? document))
         { throw new Win32Exception(Marshal.GetLastWin32Error()); }
         if(!string.Equals(document, "QQQ " + jobId, StringComparison.Ordinal))
         {
            throw new SpoolCancellationException("OWNERSHIP_MISMATCH", "The spooler number now belongs to another document; cancellation was refused.");
         }
         if(!spooler.SetJob(printer, id, JobControlDelete))
         { throw new Win32Exception(Marshal.GetLastWin32Error()); }
      }
      finally { _ = spooler.ClosePrinter(printer); }
   }
}
