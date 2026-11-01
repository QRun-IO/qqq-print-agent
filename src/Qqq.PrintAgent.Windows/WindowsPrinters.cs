/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Qqq.PrintAgent.Core.Printing;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

[assembly: InternalsVisibleTo("Qqq.PrintAgent.Cli.Tests")]

namespace Qqq.PrintAgent.Windows;

/// <summary>Windows spooler discovery, RAW ZPL, and Windows PDF renderer to native GDI.</summary>
public sealed partial class WindowsPrinters : IFilePrinterBackend
{
   /// <summary>
   /// Enumerates the queues visible to this Windows identity (for the service, the machine-wide ones). A queue whose port
   /// or driver needs a person at the keyboard (file output, print-to-PDF, XPS, fax, OneNote) is reported UNKNOWN with no
   /// formats so it can never be exposed to unattended printing. ZPL is offered only for Zebra and ZDesigner queues, and
   /// the queue's configured default media is attached when it can be read. The queue identifier is a hash of the name, so
   /// the raw queue name is never used as an identifier on the wire.
   /// </summary>
   public IReadOnlyList<PrinterInfo> Discover()
   {
      bool probeSucceeded = Native.EnumPrinters(6, null, 2, IntPtr.Zero, 0, out uint needed, out _);
      ValidateDiscoveryProbe(probeSucceeded, Marshal.GetLastWin32Error(), needed);
      if(needed == 0)
      {
         return [];
      }

      IntPtr memory = Marshal.AllocHGlobal(checked((int)needed));
      try
      {
         Check(Native.EnumPrinters(6, null, 2, memory, needed, out _, out uint count));
         List<PrinterInfo> printers = [];
         for(int i = 0; i < count; i++)
         {
            Native.PrinterInfo2 info = Marshal.PtrToStructure<Native.PrinterInfo2>(memory + i * Marshal.SizeOf<Native.PrinterInfo2>());
            string name = Marshal.PtrToStringUni(info.PrinterName) ?? "";
            string driver = Marshal.PtrToStringUni(info.DriverName) ?? "";
            bool zebra = (name + driver).Contains("Zebra", StringComparison.OrdinalIgnoreCase) || driver.Contains("ZDesigner", StringComparison.OrdinalIgnoreCase);
            string status = MapStatus(info.Status);
            string port = Marshal.PtrToStringUni(info.PortName) ?? "";
            bool interactive = RequiresInteractiveOutput(port, driver);
            string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant()[..24];
            QueueDefaultMedia? media = interactive ? null : ReadQueueMedia(id, name, driver, port);
            printers.Add(new PrinterInfo(id, name, driver, port, interactive ? "UNKNOWN" : status, interactive ? [] : zebra ? ["PDF", "ZPL"] : ["PDF"],
               MediaProfiles: media?.Profile is null ? [] : [media.Profile]));
         }
         return printers;
      }
      finally { Marshal.FreeHGlobal(memory); }
   }



   /// <summary>
   /// Only an insufficient buffer (error 122) with a reported size is an expected unsuccessful sizing probe. Any other
   /// failure is thrown, so a broken spooler can never be mistaken for an empty inventory that would withdraw every printer.
   /// </summary>
   internal static void ValidateDiscoveryProbe(bool success, int error, uint needed)
   {
      if(!success && (error != 122 || needed == 0))
      {
         throw new Win32Exception(error);
      }
   }



   /// <summary>
   /// Session-zero workers cannot answer file-save or application prompts, so ports and drivers that need one are
   /// treated as interactive and kept out of unattended printing; an unattended job would otherwise hang the spooler.
   /// </summary>
   internal static bool RequiresInteractiveOutput(string port, string driver) =>
      port.Contains("PORTPROMPT", StringComparison.OrdinalIgnoreCase) || port.Contains("FILE:", StringComparison.OrdinalIgnoreCase)
      || driver.Contains("Microsoft Print To PDF", StringComparison.OrdinalIgnoreCase) || driver.Contains("XPS", StringComparison.OrdinalIgnoreCase)
      || driver.Contains("OneNote", StringComparison.OrdinalIgnoreCase) || driver.Contains("Fax", StringComparison.OrdinalIgnoreCase);



   /// <summary>
   /// Collapses Winspool status flags into the protocol's health words. Offline outranks paused, paused outranks error,
   /// and unknown or undocumented bits are UNKNOWN rather than READY; busy, printing, warming and low-toner flags are
   /// nonblocking, so a printer that is merely working is never reported as failing.
   /// </summary>
   internal static string MapStatus(uint status)
   {
      const uint offline = 0x00000080 | 0x02000000;
      const uint paused = 0x00000001;
      // Error, deleting, paper/feeding problems, full output, unavailable, no toner,
      // failed page, intervention, memory, door open, or a required driver update.
      const uint error = 0x00000002 | 0x00000004 | 0x00000008 | 0x00000010 | 0x00000020 | 0x00000040
         | 0x00000800 | 0x00001000 | 0x00040000 | 0x00080000 | 0x00100000 | 0x00200000 | 0x00400000 | 0x04000000;
      const uint serverUnknown = 0x00800000;
      const uint knownFlags = 0x07ffffff;
      if((status & offline) != 0)
      {
         return "OFFLINE";
      }
      if((status & paused) != 0)
      {
         return "PAUSED";
      }
      if((status & error) != 0)
      {
         return "ERROR";
      }
      if((status & serverUnknown) != 0 || (status & ~knownFlags) != 0)
      {
         return "UNKNOWN";
      }
      // Busy, printing, I/O, waiting, warming, power-save and low toner are nonblocking.
      return "READY";
   }



   /// <summary>
   /// Submits exactly one server job to the queue and format the job names, after refusing anything the Windows queue
   /// cannot honor: a copy count outside 1 to 32767, paper dimensions (configured in the queue in this version) or a
   /// format the queue does not offer. Nothing is guessed or defaulted, because a wrong guess prints on the wrong stock.
   /// </summary>
   public async Task<string> PrintAsync(PrintJob job, byte[] content, CancellationToken cancellationToken)
   {
      PrinterInfo printer = Discover().Single(p => string.Equals(p.PrinterId, job.PrinterId, StringComparison.Ordinal));
      if(job.Options.Copies is < 1 or > 32767)
      {
         throw new InvalidDataException("Copies must be between 1 and 32767.");
      }
      if(job.Options.PaperWidthMm is not null || job.Options.PaperHeightMm is not null)
      {
         throw new InvalidDataException("Configure paper dimensions in the Windows queue for this version.");
      }
      if(!printer.Formats.Contains(job.Format, StringComparer.Ordinal))
      {
         throw new InvalidDataException("The queue does not support this document format.");
      }

      cancellationToken.ThrowIfCancellationRequested();
      return job.Format switch
      {
         "ZPL" => PrintRaw(printer, job, content),
         "PDF" => await PrintPdfAsync(printer, job, content, cancellationToken).ConfigureAwait(false),
         _ => throw new InvalidDataException("Unsupported document format."),
      };
   }



   /// <summary>
   /// Sends printer-ready ZPL as one RAW spool document, repeating the bytes per copy and continuing after partial writes
   /// until the spooler has taken them all. The queue's media is rechecked immediately before the document starts, and any
   /// failure after it starts aborts the document so half a label is not left queued.
   /// </summary>
   private static string PrintRaw(PrinterInfo selectedPrinter, PrintJob job, byte[] content)
   {
      Check(Native.OpenPrinter(selectedPrinter.Name, out IntPtr printer, IntPtr.Zero));
      try
      {
         _ = RequireCurrentMedia(selectedPrinter, job);
         Native.DocInfo1 info = new() { DocumentName = $"QQQ {job.JobId}", DataType = "RAW" };
         uint id = Native.StartDocPrinter(printer, 1, ref info);
         if(id == 0)
         {
            throw new Win32Exception(Marshal.GetLastWin32Error());
         }

         try
         {
            for(int copy = 0; copy < job.Options.Copies; copy++)
            {
               int offset = 0;
               while(offset < content.Length)
               {
                  byte[] remaining = content[offset..];
                  Check(Native.WritePrinter(printer, remaining, remaining.Length, out int written));
                  if(written == 0)
                  {
                     throw new IOException("Printer accepted zero bytes.");
                  }

                  offset += written;
               }
            }
            Check(Native.EndDocPrinter(printer));
            return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
         }
         catch { Native.AbortPrinter(printer); throw; }
      }
      finally { Native.ClosePrinter(printer); }
   }



   /// <summary>
   /// Loads PDF bytes into the Windows PDF renderer through an in-memory stream, so the document never touches disk, and
   /// hands the loaded document to the shared page printer.
   /// </summary>
   private static async Task<string> PrintPdfAsync(PrinterInfo printer, PrintJob job, byte[] content, CancellationToken cancellationToken)
   {
      using InMemoryRandomAccessStream pdfStream = new();
      using(DataWriter writer = new(pdfStream))
      {
         writer.WriteBytes(content);
         await writer.StoreAsync();
         writer.DetachStream();
      }
      pdfStream.Seek(0);
      PdfDocument document = await PdfDocument.LoadFromStreamAsync(pdfStream);
      return await PrintPdfDocumentAsync(printer, job, document, cancellationToken).ConfigureAwait(false);
   }



   /// <summary>
   /// Owns the native document lifecycle for a loaded PDF. It rechecks the selected media, prepares a job-only copy of the
   /// driver settings, opens a device context and starts the document only after the context's size and resolution match
   /// the selection. Pages are rendered and printed copy by copy; any failure after the document starts aborts it, and the
   /// context is always released.
   /// </summary>
   private static async Task<string> PrintPdfDocumentAsync(PrinterInfo printer, PrintJob job, PdfDocument document, CancellationToken cancellationToken)
   {
      QueueDefaultMedia? media = RequireCurrentMedia(printer, job);
      byte[]? documentMode = media is null ? null : PreparePdfMode(printer, media);
      IntPtr dc = CreateDocumentContext(printer.Name, documentMode);
      if(dc == IntPtr.Zero)
      {
         throw new Win32Exception(Marshal.GetLastWin32Error());
      }

      try
      {
         Native.DocInfo info = new() { Size = Marshal.SizeOf<Native.DocInfo>(), DocumentName = $"QQQ {job.JobId}" };
         int jobId = StartValidatedPdfDocument(documentMode, media?.Profile,
            capability => Native.GetDeviceCaps(dc, capability), () => Native.StartDoc(dc, ref info));
         if(jobId <= 0)
         {
            throw new Win32Exception(Marshal.GetLastWin32Error());
         }

         try
         {
            for(int copy = 0; copy < job.Options.Copies; copy++)
            {
               for(uint pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
               {
                  cancellationToken.ThrowIfCancellationRequested();
                  using PdfPage page = document.GetPage(pageIndex);
                  await PrintPageAsync(dc, page, job.Options.Scale).ConfigureAwait(false);
               }
            }

            if(Native.EndDoc(dc) <= 0)
            {
               throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return jobId.ToString(System.Globalization.CultureInfo.InvariantCulture);
         }
         catch
         {
            // Cleanup is best effort; preserve the original submission error.
            _ = Native.AbortDoc(dc);
            throw;
         }
      }
      finally { Native.DeleteDC(dc); }
   }



   /// <summary>
   /// Renders one page to a bounded bitmap (at most 150 dpi equivalent and 5000 pixels on the long side, so a hostile PDF
   /// cannot exhaust memory) and draws it at its true physical size on the driver page, or scaled to fit and centered
   /// when the job asks for FIT. Invalid page dimensions are refused before any rendering.
   /// </summary>
   private static async Task PrintPageAsync(IntPtr dc, PdfPage page, string scaleMode)
   {
      if(!double.IsFinite(page.Size.Width) || !double.IsFinite(page.Size.Height) || page.Size.Width <= 0 || page.Size.Height <= 0)
      {
         throw new InvalidDataException("PDF page dimensions are invalid.");
      }
      double renderScale = Math.Min(150d / 96, 5000 / Math.Max(page.Size.Width, page.Size.Height));
      using InMemoryRandomAccessStream rendered = new();
      await page.RenderToStreamAsync(rendered, new PdfPageRenderOptions
      {
         DestinationWidth = (uint)Math.Max(1, page.Size.Width * renderScale),
         DestinationHeight = (uint)Math.Max(1, page.Size.Height * renderScale),
      });
      rendered.Seek(0);
      BitmapDecoder decoder = await BitmapDecoder.CreateAsync(rendered);
      PixelDataProvider pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
      byte[] data = pixels.DetachPixelData();
      int width = checked((int)(page.Size.Width / 96 * Native.GetDeviceCaps(dc, 88)));
      int height = checked((int)(page.Size.Height / 96 * Native.GetDeviceCaps(dc, 90)));
      int x = -Native.GetDeviceCaps(dc, 112);
      int y = -Native.GetDeviceCaps(dc, 113);
      if(string.Equals(scaleMode, "FIT", StringComparison.Ordinal))
      {
         int availableWidth = Native.GetDeviceCaps(dc, 8);
         int availableHeight = Native.GetDeviceCaps(dc, 10);
         double factor = Math.Min((double)availableWidth / width, (double)availableHeight / height);
         width = (int)(width * factor);
         height = (int)(height * factor);
         x = (availableWidth - width) / 2;
         y = (availableHeight - height) / 2;
      }
      Native.BitmapInfo bitmap = new() { HeaderSize = 40, Width = (int)decoder.PixelWidth, Height = -(int)decoder.PixelHeight, Planes = 1, BitCount = 32 };
      Check(Native.StartPage(dc) > 0);
      Check(Native.SetStretchBltMode(dc, 4) != 0);
      int result = Native.StretchDIBits(dc, x, y, width, height, 0, 0, (int)decoder.PixelWidth, (int)decoder.PixelHeight, data, ref bitmap, 0, 0x00CC0020);
      Check(result is not (0 or -1));
      Check(Native.EndPage(dc) > 0);
   }



   /// <summary>Converts a failed Win32 call into an exception carrying the last error, so the worker reports the real cause instead of continuing.</summary>
   private static void Check(bool success)
   {
      if(!success)
      {
         throw new Win32Exception(Marshal.GetLastWin32Error());
      }
   }



   /// <summary>
   /// The Win32 spooler and GDI entry points this adapter calls. Every declaration mirrors the documented native
   /// signature exactly; the layouts below are marshaled by the runtime, so field order and types must not change.
   /// </summary>
   private static partial class Native
   {
      /// <summary>PRINTER_INFO_2 as returned by EnumPrinters level 2: queue, driver and port names, status and the machine default settings pointer.</summary>
      [StructLayout(LayoutKind.Sequential)]
      internal struct PrinterInfo2
      {
         internal IntPtr ServerName, PrinterName, ShareName, PortName, DriverName, Comment, Location, DevMode, SepFile, PrintProcessor, DataType, Parameters, SecurityDescriptor;
         internal uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, Jobs, AveragePpm;
      }



      /// <summary>DOC_INFO_1 for StartDocPrinter: the spool document's name and data type (RAW for ZPL).</summary>
      [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
      internal struct DocInfo1
      {
         [MarshalAs(UnmanagedType.LPWStr)] internal string DocumentName;
         [MarshalAs(UnmanagedType.LPWStr)] internal string? OutputFile;
         [MarshalAs(UnmanagedType.LPWStr)] internal string DataType;
      }



      /// <summary>DOCINFO for GDI StartDoc: the document name shown in the spooler for a rendered PDF.</summary>
      [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
      internal struct DocInfo
      {
         internal int Size;
         [MarshalAs(UnmanagedType.LPWStr)] internal string DocumentName;
         [MarshalAs(UnmanagedType.LPWStr)] internal string? OutputFile;
         [MarshalAs(UnmanagedType.LPWStr)] internal string? DataType;
         internal uint Type;
      }



      /// <summary>BITMAPINFOHEADER used with StretchDIBits to draw the rendered page bitmap.</summary>
      [StructLayout(LayoutKind.Sequential)]
      internal struct BitmapInfo
      {
         internal uint HeaderSize;
         internal int Width, Height;
         internal ushort Planes, BitCount;
         internal uint Compression, ImageSize;
         internal int XPelsPerMeter, YPelsPerMeter;
         internal uint ColorsUsed, ColorsImportant;
      }



      /// <summary>Lists the installed queues (level 2) so discovery can read their names, drivers, ports and status.</summary>
      [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr buffer, uint size, out uint needed, out uint returned);



      /// <summary>Opens a queue by name for a spool document or a settings read; the handle must be closed.</summary>
      [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);



      /// <summary>Starts one RAW spool document and returns its spooler job number, or zero on failure.</summary>
      [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern uint StartDocPrinter(IntPtr printer, int level, ref DocInfo1 info);



      /// <summary>Writes bytes into the open spool document; the written count can be less than requested.</summary>
      [DllImport("winspool.drv", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WritePrinter(IntPtr printer, byte[] bytes, int count, out int written);



      /// <summary>Finishes the spool document so the spooler may send it to the printer.</summary>
      [DllImport("winspool.drv", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EndDocPrinter(IntPtr printer);



      /// <summary>Releases a queue handle opened by OpenPrinter.</summary>
      [DllImport("winspool.drv", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ClosePrinter(IntPtr printer);



      /// <summary>Discards a spool document that failed part way, so a partial label is not printed.</summary>
      [DllImport("winspool.drv", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AbortPrinter(IntPtr printer);



      /// <summary>Creates a printer device context from the supplied settings so the real page size and resolution can be read and drawn on.</summary>
      [DllImport("gdi32.dll", EntryPoint = "CreateDCW", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr devMode);



      /// <summary>Starts a GDI document on a device context and returns its spooler job number.</summary>
      [DllImport("gdi32.dll", EntryPoint = "StartDocW", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern int StartDoc(IntPtr dc, ref DocInfo info);



      /// <summary>Reads one capability of a device context, such as resolution, printable size or margins.</summary>
      [DllImport("gdi32.dll")] internal static extern int GetDeviceCaps(IntPtr dc, int index);



      /// <summary>Starts a page in the open GDI document.</summary>
      [DllImport("gdi32.dll", SetLastError = true)] internal static extern int StartPage(IntPtr dc);



      /// <summary>Ends the current page so the driver can emit it.</summary>
      [DllImport("gdi32.dll", SetLastError = true)] internal static extern int EndPage(IntPtr dc);



      /// <summary>Ends the GDI document and submits it to the spooler.</summary>
      [DllImport("gdi32.dll", SetLastError = true)] internal static extern int EndDoc(IntPtr dc);



      /// <summary>Cancels the GDI document after a failure, so nothing partial is printed.</summary>
      [DllImport("gdi32.dll", SetLastError = true)] internal static extern int AbortDoc(IntPtr dc);



      /// <summary>Deletes a device context created by CreateDC.</summary>
      [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(IntPtr dc);



      /// <summary>Selects the stretch mode used when the page bitmap is scaled onto the driver page.</summary>
      [DllImport("gdi32.dll")] internal static extern int SetStretchBltMode(IntPtr dc, int mode);



      /// <summary>Draws the rendered page bitmap onto the page at the computed position and size.</summary>
      [DllImport("gdi32.dll", SetLastError = true)] internal static extern int StretchDIBits(IntPtr dc, int x, int y, int width, int height, int sx, int sy, int sw, int sh, byte[] bits, ref BitmapInfo info, uint usage, uint operation);
   }
}

