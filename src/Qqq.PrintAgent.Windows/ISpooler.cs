/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The Windows spooler calls that listing queues and sending one RAW document need. Each member is one Windows call and
/// reports what Windows answered; a call that fails returns false (or a job number of zero) and leaves its Windows error
/// as the thread's last error (<c>Marshal.GetLastWin32Error</c>), exactly as the native call does. Which queues may print
/// unattended, what a document is called, how its bytes are written and when it is aborted is decided by
/// <see cref="WindowsPrinters"/>.
/// </summary>
internal interface ISpooler
{
   /// <summary>
   /// Asks how many bytes the list of queues selected by the flags needs (<c>EnumPrinters</c> level 2 with no buffer).
   /// Windows answers this sizing call with false, error 122 and a size when there are queues to list.
   /// </summary>
   bool EnumPrintersSize(uint flags, out uint needed);



   /// <summary>
   /// Lists the queues selected by the flags into a buffer of the given size (<c>EnumPrinters</c> level 2) and reports each
   /// queue's name, driver, port and status as a plain record. The list is empty when the call failed.
   /// </summary>
   bool EnumPrinters(uint flags, int size, out IReadOnlyList<SpoolerQueue> queues);



   /// <summary>Opens a queue by name with default access (<c>OpenPrinter</c>). The handle must be closed with <see cref="ClosePrinter"/>.</summary>
   bool OpenPrinter(string name, out IntPtr printer);



   /// <summary>
   /// Starts one spool document with the given name and data type (<c>StartDocPrinter</c> level 1, no output file) and
   /// returns its spooler job number, or zero when Windows refused it.
   /// </summary>
   uint StartDocPrinter(IntPtr printer, string documentName, string dataType);



   /// <summary>Writes the first <paramref name="count"/> bytes into the open document (<c>WritePrinter</c>) and reports how many the spooler took, which can be fewer.</summary>
   bool WritePrinter(IntPtr printer, byte[] bytes, int count, out int written);



   /// <summary>Finishes the open document so the spooler may send it to the printer (<c>EndDocPrinter</c>).</summary>
   bool EndDocPrinter(IntPtr printer);



   /// <summary>Discards the open document (<c>AbortPrinter</c>), so nothing partial is printed.</summary>
   bool AbortPrinter(IntPtr printer);



   /// <summary>Releases a queue handle opened by <see cref="OpenPrinter"/> (<c>ClosePrinter</c>).</summary>
   bool ClosePrinter(IntPtr printer);
}
