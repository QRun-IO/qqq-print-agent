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
   /// <summary>Enumerate installed queues visible to this Windows identity.</summary>
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

   /// <summary>Only an insufficient buffer is an expected unsuccessful sizing probe.</summary>
   internal static void ValidateDiscoveryProbe(bool success, int error, uint needed)
   {
      if(!success && (error != 122 || needed == 0))
      {
         throw new Win32Exception(error);
      }
   }

   /// <summary>Session-zero workers cannot answer file-save or application prompts.</summary>
   internal static bool RequiresInteractiveOutput(string port, string driver) =>
      port.Contains("PORTPROMPT", StringComparison.OrdinalIgnoreCase) || port.Contains("FILE:", StringComparison.OrdinalIgnoreCase)
      || driver.Contains("Microsoft Print To PDF", StringComparison.OrdinalIgnoreCase) || driver.Contains("XPS", StringComparison.OrdinalIgnoreCase)
      || driver.Contains("OneNote", StringComparison.OrdinalIgnoreCase) || driver.Contains("Fax", StringComparison.OrdinalIgnoreCase);

   /// <summary>Collapse Winspool flags into protocol health without treating activity as failure.</summary>
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

   /// <summary>Submit exactly one server job using its explicit queue and format.</summary>
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

   /// <summary>Send printer-ready ZPL bytes, handling partial spooler writes.</summary>
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

   /// <summary>Own the native document lifecycle while rendering its PDF pages.</summary>
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

   /// <summary>Render a bounded bitmap and map its physical size onto the driver page.</summary>
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

   /// <summary>Convert a Win32 failure to an error the worker can report.</summary>
   private static void Check(bool success)
   {
      if(!success)
      {
         throw new Win32Exception(Marshal.GetLastWin32Error());
      }
   }

   private static partial class Native
   {
      [StructLayout(LayoutKind.Sequential)]
      internal struct PrinterInfo2
      {
         internal IntPtr ServerName, PrinterName, ShareName, PortName, DriverName, Comment, Location, DevMode, SepFile, PrintProcessor, DataType, Parameters, SecurityDescriptor;
         internal uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, Jobs, AveragePpm;
      }
      [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
      internal struct DocInfo1
      {
         [MarshalAs(UnmanagedType.LPWStr)] internal string DocumentName;
         [MarshalAs(UnmanagedType.LPWStr)] internal string? OutputFile;
         [MarshalAs(UnmanagedType.LPWStr)] internal string DataType;
      }
      [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
      internal struct DocInfo
      {
         internal int Size;
         [MarshalAs(UnmanagedType.LPWStr)] internal string DocumentName;
         [MarshalAs(UnmanagedType.LPWStr)] internal string? OutputFile;
         [MarshalAs(UnmanagedType.LPWStr)] internal string? DataType;
         internal uint Type;
      }
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
      [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr buffer, uint size, out uint needed, out uint returned);
      [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);
      [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern uint StartDocPrinter(IntPtr printer, int level, ref DocInfo1 info);
      [DllImport("winspool.drv", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WritePrinter(IntPtr printer, byte[] bytes, int count, out int written);
      [DllImport("winspool.drv", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EndDocPrinter(IntPtr printer);
      [DllImport("winspool.drv", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ClosePrinter(IntPtr printer);
      [DllImport("winspool.drv", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AbortPrinter(IntPtr printer);
      [DllImport("gdi32.dll", EntryPoint = "CreateDCW", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr devMode);
      [DllImport("gdi32.dll", EntryPoint = "StartDocW", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern int StartDoc(IntPtr dc, ref DocInfo info);
      [DllImport("gdi32.dll")] internal static extern int GetDeviceCaps(IntPtr dc, int index);
      [DllImport("gdi32.dll", SetLastError = true)] internal static extern int StartPage(IntPtr dc);
      [DllImport("gdi32.dll", SetLastError = true)] internal static extern int EndPage(IntPtr dc);
      [DllImport("gdi32.dll", SetLastError = true)] internal static extern int EndDoc(IntPtr dc);
      [DllImport("gdi32.dll", SetLastError = true)] internal static extern int AbortDoc(IntPtr dc);
      [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(IntPtr dc);
      [DllImport("gdi32.dll")] internal static extern int SetStretchBltMode(IntPtr dc, int mode);
      [DllImport("gdi32.dll", SetLastError = true)] internal static extern int StretchDIBits(IntPtr dc, int x, int y, int width, int height, int sx, int sy, int sw, int sh, byte[] bits, ref BitmapInfo info, uint usage, uint operation);
   }
}

