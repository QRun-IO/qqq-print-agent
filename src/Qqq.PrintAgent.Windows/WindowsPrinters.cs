/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Qqq.PrintAgent.Core.Printing;

[assembly: InternalsVisibleTo("Qqq.PrintAgent.Cli.Tests")]

namespace Qqq.PrintAgent.Windows;

/// <summary>Windows spooler discovery, RAW ZPL, and Windows PDF renderer to native GDI.</summary>
public sealed partial class WindowsPrinters : IPrinterBackend
{
   private readonly ISpooler _spooler;
   private readonly IPrinterSettings _settings;
   private readonly IGdiPrinter _gdi;
   private readonly IPdfRenderer _pdf;



   /// <summary>Builds the adapter over the real Windows spooler, printer settings, GDI and PDF renderer. Nothing is asked of Windows until a member is called.</summary>
   public WindowsPrinters() : this(new WindowsSpooler(), new WindowsPrinterSettings(), new WindowsGdiPrinter(), new WindowsPdfRenderer())
   {
   }



   /// <summary>
   /// Builds the adapter over the given Windows calls. Every rule in this class reaches Windows only through these, so
   /// tests run the rules against fakes that record what was asked and never open a printer.
   /// </summary>
   internal WindowsPrinters(ISpooler spooler, IPrinterSettings settings, IGdiPrinter gdi, IPdfRenderer pdf)
   {
      _spooler = spooler;
      _settings = settings;
      _gdi = gdi;
      _pdf = pdf;
   }



   /// <summary>
   /// Lists the queues visible to this Windows identity (for the service, the machine-wide ones), each with the queue's
   /// configured default media when it can be read. A queue whose port or driver needs a person at the keyboard (file
   /// output, print-to-PDF, XPS, fax, OneNote) is reported UNKNOWN with no formats so it can never be exposed to
   /// unattended printing. ZPL is offered only for Zebra and ZDesigner queues. The queue identifier is a hash of the
   /// name, so the raw queue name is never used as an identifier on the wire.
   /// <para>
   /// Native calls: the two enumeration calls, then for every queue that can print unattended, in the order the spooler
   /// lists them, the settings read (open the queue, its level 8 details, the driver's paper lists, a device context
   /// for the resolution). So every such queue's driver is asked, and one that hangs holds the whole list.
   /// </para>
   /// </summary>
   public IReadOnlyList<PrinterInfo> Discover() => [.. EnumerateQueues().Select(queue => WithMedia(Identify(queue)))];



   /// <summary>
   /// Lists the same queues as <see cref="Discover"/>, identified and classified by the same rule, without their media.
   /// Native calls: the two enumeration calls and nothing else. This process opens no queue and asks no driver, so a
   /// queue whose driver hangs or fails neither slows this nor fails it. What the spooler does to answer the
   /// enumeration is its own: a connection to a print server that does not answer can still hold it, as
   /// <see cref="EnumerateQueues"/> says.
   /// </summary>
   public IReadOnlyList<PrinterInfo> ListWithoutMedia() => [.. EnumerateQueues().Select(Identify)];



   /// <summary>
   /// The one queue with this identifier, exactly as <see cref="Discover"/> lists it, or null when no queue has it. The
   /// identifier of each listed queue is worked out from its name, which needs no native call, and only the queue that
   /// matches has its settings read. Native calls: the two enumeration calls, then the settings read of that one queue
   /// when it can print unattended; this process opens no other queue and asks no other driver. Two queues with one
   /// identifier are refused before either is read, because there is no telling which was meant.
   /// </summary>
   public PrinterInfo? FindWithMedia(string printerId)
   {
      PrinterInfo? queue = EnumerateQueues().Select(Identify).SingleOrDefault(item => string.Equals(item.PrinterId, printerId, StringComparison.Ordinal));
      return queue is null ? null : WithMedia(queue);
   }



   /// <summary>
   /// Whether the text has the form of a queue identifier as <see cref="QueueIdentifier"/> makes them: exactly 24
   /// hexadecimal digits in lower case. Nothing else can name a queue, so anything else is refused, or read as absent,
   /// before it is used.
   /// </summary>
   internal static bool IsQueueIdentifier([NotNullWhen(true)] string? text)
      => text is { Length: 24 } && text.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));



   /// <summary>The identifier of the queue with this name: the first 24 hexadecimal digits, in lower case, of the SHA-256 of the name in UTF-8.</summary>
   internal static string QueueIdentifier(string name) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant()[..24];



   /// <summary>
   /// Asks the spooler for its list of queues with the two enumeration calls every read begins with: one for the size of
   /// the list and one for the list. A spooler with nothing installed answers the first with no size, and the second is
   /// not made. These are the only calls a read makes that concern every queue at once.
   /// <para>
   /// This process opens no queue for them, but the spooler may. Both calls ask for the queues installed here and for
   /// the connections this identity has to printers on print servers, at the level that carries each queue's driver,
   /// port and status (level 2). Microsoft documents that an enumeration at that level opens every such connection, and
   /// waits for the remote call to time out when the connection is down, its server no longer exists or its printer no
   /// longer exists (the remarks on PRINTER_INFO_4). So one connection to a print server that is switched off or
   /// cannot be reached can hold every read that begins here, the list without media included, although no driver on
   /// this computer is asked. That was so before the three reads existed and they do not change it. It has not been
   /// measured on Windows. Listing at another level would change what reaches Windows, so it is not done here; it
   /// belongs with the check on real printers. docs/REALTIME.md states the limit for operators.
   /// </para>
   /// </summary>
   private IReadOnlyList<SpoolerQueue> EnumerateQueues()
   {
      ////////////////////////////////////////////////////////////////////
      // 6 is PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS: the queues //
      // installed here and the connections this identity has           //
      ////////////////////////////////////////////////////////////////////
      bool probeSucceeded = _spooler.EnumPrintersSize(6, out uint needed);
      ValidateDiscoveryProbe(probeSucceeded, Marshal.GetLastWin32Error(), needed);
      if(needed == 0)
      {
         return [];
      }

      Check(_spooler.EnumPrinters(6, checked((int)needed), out IReadOnlyList<SpoolerQueue> queues));
      return queues;
   }



   /// <summary>
   /// Turns one line of the spooler's list into the queue as it is reported, without its media and without a native
   /// call: the identifier from the name, the health word from the status flags, and the formats from what the name,
   /// driver and port say. A queue that needs a person is UNKNOWN with no formats. This is the one mapping behind all
   /// three reads, so they cannot disagree about a queue.
   /// </summary>
   internal static PrinterInfo Identify(SpoolerQueue queue)
   {
      string name = queue.Name ?? "";
      string driver = queue.Driver ?? "";
      bool zebra = (name + driver).Contains("Zebra", StringComparison.OrdinalIgnoreCase) || driver.Contains("ZDesigner", StringComparison.OrdinalIgnoreCase);
      string status = MapStatus(queue.Status);
      string port = queue.Port ?? "";
      bool interactive = RequiresInteractiveOutput(port, driver);
      return new PrinterInfo(QueueIdentifier(name), name, driver, port, interactive ? "UNKNOWN" : status, interactive ? [] : zebra ? ["PDF", "ZPL"] : ["PDF"]);
   }



   /// <summary>
   /// Adds the queue's configured default media to an identified queue, which is the step that asks its driver. A queue
   /// with no formats is one that needs a person: it is never opened and has no media.
   /// </summary>
   private PrinterInfo WithMedia(PrinterInfo queue)
   {
      QueueDefaultMedia? media = queue.Formats.Length == 0 ? null : ReadQueueMedia(queue.PrinterId, queue.Name, queue.DriverName, queue.PortName);
      return queue with { MediaProfiles = media?.Profile is null ? [] : [media.Profile] };
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
   /// Owns the native document lifecycle for a loaded PDF. It rechecks the selected media, prepares a job-only copy of the
   /// driver settings, opens a device context and starts the document only after the context's size and resolution match
   /// the selection. Pages are rendered and printed copy by copy; any failure after the document starts aborts it, and the
   /// context is always released.
   /// </summary>
   private async Task<string> PrintPdfDocumentAsync(PrinterInfo printer, PrintJob job, IPdfDocument document, CancellationToken cancellationToken)
   {
      (int WidthMicrons, int HeightMicrons)? paper = job.Options.CustomPaperMicrons();
      QueueDefaultMedia? media = RequireCurrentMedia(printer, job);
      byte[]? documentMode = paper is { } jobPaper ? PrepareCustomPdfMode(printer, jobPaper) : media is null ? null : PreparePdfMode(printer, media);
      IntPtr dc = CreateDocumentContext(printer.Name, documentMode);
      if(dc == IntPtr.Zero)
      {
         throw new Win32Exception(Marshal.GetLastWin32Error());
      }

      try
      {
         // Explicit job paper is checked against the device context's physical size and dpi, so a driver that
         // substitutes its 4 x 6 default fails before StartDoc instead of feeding the wrong length.
         PrinterMediaProfile? expected = paper is { } requested
            ? new PrinterMediaProfile("job-paper", "job-paper", "Job paper", requested.WidthMicrons, requested.HeightMicrons, _gdi.GetDeviceCaps(dc, 88), _gdi.GetDeviceCaps(dc, 90))
            : media?.Profile;
         int jobId = StartValidatedPdfDocument(documentMode, expected,
            capability => _gdi.GetDeviceCaps(dc, capability), () => _gdi.StartDoc(dc, $"QQQ {job.JobId}"));
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
                  using IPdfPage page = document.GetPage(pageIndex);
                  await PrintPageAsync(dc, page, job.Options.Scale).ConfigureAwait(false);
               }
            }

            if(_gdi.EndDoc(dc) <= 0)
            {
               throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return jobId.ToString(System.Globalization.CultureInfo.InvariantCulture);
         }
         catch
         {
            // Cleanup is best effort; preserve the original submission error.
            _ = _gdi.AbortDoc(dc);
            throw;
         }
      }
      finally { _ = _gdi.DeleteDC(dc); }
   }



   /// <summary>
   /// Renders one page to a bounded bitmap (at most 150 dpi equivalent and 5000 pixels on the long side, so a hostile PDF
   /// cannot exhaust memory) and draws it at its true physical size on the driver page, or scaled to fit and centered
   /// when the job asks for FIT. Invalid page dimensions are refused before any rendering.
   /// </summary>
   private async Task PrintPageAsync(IntPtr dc, IPdfPage page, string scaleMode)
   {
      if(!double.IsFinite(page.Width) || !double.IsFinite(page.Height) || page.Width <= 0 || page.Height <= 0)
      {
         throw new InvalidDataException("PDF page dimensions are invalid.");
      }
      double renderScale = Math.Min(150d / 96, 5000 / Math.Max(page.Width, page.Height));
      PageBitmap bitmap = await page.RenderAsync((uint)Math.Max(1, page.Width * renderScale), (uint)Math.Max(1, page.Height * renderScale)).ConfigureAwait(false);
      int width = checked((int)(page.Width / 96 * _gdi.GetDeviceCaps(dc, 88)));
      int height = checked((int)(page.Height / 96 * _gdi.GetDeviceCaps(dc, 90)));
      int x = -_gdi.GetDeviceCaps(dc, 112);
      int y = -_gdi.GetDeviceCaps(dc, 113);
      if(string.Equals(scaleMode, "FIT", StringComparison.Ordinal))
      {
         int availableWidth = _gdi.GetDeviceCaps(dc, 8);
         int availableHeight = _gdi.GetDeviceCaps(dc, 10);
         double factor = Math.Min((double)availableWidth / width, (double)availableHeight / height);
         width = (int)(width * factor);
         height = (int)(height * factor);
         x = (availableWidth - width) / 2;
         y = (availableHeight - height) / 2;
      }
      ///////////////////////////////////////////////////////////////
      // a negative height tells GDI the first row is the top one; //
      // one plane, 32 bits a pixel                                //
      ///////////////////////////////////////////////////////////////
      GdiBitmapHeader header = new((int)bitmap.PixelWidth, -(int)bitmap.PixelHeight, 1, 32);
      Check(_gdi.StartPage(dc) > 0);
      ///////////////////////////////////////////////////////////////////
      // 4 is HALFTONE; 0 is DIB_RGB_COLORS and 0x00CC0020 is SRCCOPY: //
      // the whole bitmap, copied as it is                             //
      ///////////////////////////////////////////////////////////////////
      Check(_gdi.SetStretchBltMode(dc, 4) != 0);
      int result = _gdi.StretchDIBits(dc, x, y, width, height, 0, 0, (int)bitmap.PixelWidth, (int)bitmap.PixelHeight, bitmap.Pixels, header, 0, 0x00CC0020);
      Check(result is not (0 or -1));
      Check(_gdi.EndPage(dc) > 0);
   }



   /// <summary>Converts a failed Win32 call into an exception carrying the last error, so the worker reports the real cause instead of continuing.</summary>
   private static void Check(bool success)
   {
      if(!success)
      {
         throw new Win32Exception(Marshal.GetLastWin32Error());
      }
   }
}
