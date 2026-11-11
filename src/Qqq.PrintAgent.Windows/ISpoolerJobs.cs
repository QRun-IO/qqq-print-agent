/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The Windows spooler calls that cancelling one job needs: open a queue, read a job's document name, send the job a
/// command and close the queue. Each member is one Windows call and reports what Windows answered; a call that fails
/// returns false and leaves its Windows error as the thread's last error (<c>Marshal.GetLastWin32Error</c>), exactly as
/// the native call does. Whether a job is ours, whether it is already gone and whether it may be deleted is decided by
/// <see cref="WindowsSpoolerJobs"/>.
/// </summary>
internal interface ISpoolerJobs
{
   /// <summary>Opens a queue by name with default access (<c>OpenPrinter</c>). The handle must be closed with <see cref="ClosePrinter"/>.</summary>
   bool OpenPrinter(string name, out IntPtr printer);



   /// <summary>
   /// Asks how many bytes a job's level 1 details need (<c>GetJob</c> with no buffer). Windows answers this sizing call
   /// with false and a size; a size of zero means the job could not be read at all, and the last error says why.
   /// </summary>
   bool GetJobSize(IntPtr printer, uint job, out uint needed);



   /// <summary>
   /// Reads a job's level 1 details into a buffer of the given size (<c>GetJob</c>) and reports its document name, which is
   /// null when Windows stored none. The name is read only when the call succeeded.
   /// </summary>
   bool GetJobDocument(IntPtr printer, uint job, uint size, out string? document);



   /// <summary>Sends one control command to a job (<c>SetJob</c> with no job details), such as 5 to delete it.</summary>
   bool SetJob(IntPtr printer, uint job, uint command);



   /// <summary>Releases a queue handle opened by <see cref="OpenPrinter"/> (<c>ClosePrinter</c>).</summary>
   bool ClosePrinter(IntPtr printer);
}
