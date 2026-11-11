/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The Windows GDI calls that a printer device context needs: create one from a queue's settings, read what it reports
/// about its page, and print a document of bitmap pages on it. Each member is one Windows call and returns what Windows
/// returned; a call that fails leaves its Windows error as the thread's last error (<c>Marshal.GetLastWin32Error</c>),
/// exactly as the native call does. Which settings a context is built from, whether its page matches the selected media,
/// where and how large each page is drawn, how often it is repeated and when a document is aborted is decided by
/// <see cref="WindowsPrinters"/>.
/// </summary>
internal interface IGdiPrinter
{
   /// <summary>
   /// Creates a device context for a queue (<c>CreateDC</c>, no output override) from the given complete settings, or from
   /// the queue's current defaults when the settings are null. It returns zero when Windows refused. The context must be
   /// released with <see cref="DeleteDC"/>.
   /// </summary>
   IntPtr CreateDC(string driver, string device, byte[]? devMode);



   /// <summary>Reads one value a device context reports about itself (<c>GetDeviceCaps</c>), such as 88 and 90 for resolution or 110 and 111 for the physical page.</summary>
   int GetDeviceCaps(IntPtr dc, int index);



   /// <summary>Releases a device context created by <see cref="CreateDC"/> (<c>DeleteDC</c>).</summary>
   bool DeleteDC(IntPtr dc);



   /// <summary>
   /// Starts a document with the given name on a device context (<c>StartDoc</c>, no output file or data type) and returns
   /// its spooler job number, or a value of zero or less when Windows refused it.
   /// </summary>
   int StartDoc(IntPtr dc, string documentName);



   /// <summary>Starts a page in the open document (<c>StartPage</c>); a value of zero or less is a failure.</summary>
   int StartPage(IntPtr dc);



   /// <summary>Selects how a bitmap is resampled when it is stretched (<c>SetStretchBltMode</c>); zero is a failure.</summary>
   int SetStretchBltMode(IntPtr dc, int mode);



   /// <summary>
   /// Draws the given part of a bitmap, described by its header, into the given rectangle of the page (<c>StretchDIBits</c>)
   /// with the given colour usage and raster operation. Zero or -1 is a failure.
   /// </summary>
   int StretchDIBits(IntPtr dc, int x, int y, int width, int height, int sourceX, int sourceY, int sourceWidth, int sourceHeight, byte[] bits, GdiBitmapHeader header, uint usage, uint operation);



   /// <summary>Ends the current page so the driver can emit it (<c>EndPage</c>); a value of zero or less is a failure.</summary>
   int EndPage(IntPtr dc);



   /// <summary>Ends the document and hands it to the spooler (<c>EndDoc</c>); a value of zero or less is a failure.</summary>
   int EndDoc(IntPtr dc);



   /// <summary>Cancels the open document (<c>AbortDoc</c>), so nothing partial is printed.</summary>
   int AbortDoc(IntPtr dc);
}
