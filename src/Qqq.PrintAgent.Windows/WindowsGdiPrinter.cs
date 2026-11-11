/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The real Windows GDI behind <see cref="IGdiPrinter"/>: each member makes one call into gdi32.dll and returns what it
/// returned. It decides nothing about pages or documents.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Calls CreateDC, GetDeviceCaps, DeleteDC, StartDoc, StartPage, SetStretchBltMode, StretchDIBits, EndPage, EndDoc and AbortDoc in gdi32.dll on a printer device context. A test cannot run these without an installed Windows print queue and its driver, and StartDoc and the page calls send a document to that queue, which tests must never do. No rule lives here: which settings a context is created from, the media check before StartDoc, the document name, the position, size and scaling of each page, the stretch mode and raster operation, the copy and page loops, and when a document is ended or aborted are decided in WindowsPrinters, which is tested with a fake of IGdiPrinter. Pinning the settings for CreateDC, null included, is done by WindowsPrinters.WithPinnedMode, which is covered.")]
internal sealed class WindowsGdiPrinter : IGdiPrinter
{
   /// <summary>Calls CreateDC with no output override and the settings pinned for the length of the call; null settings are passed as a null pointer.</summary>
   public IntPtr CreateDC(string driver, string device, byte[]? devMode) => WindowsPrinters.WithPinnedMode(devMode, pointer => Native.CreateDC(driver, device, null, pointer));



   /// <summary>Calls GetDeviceCaps.</summary>
   public int GetDeviceCaps(IntPtr dc, int index) => Native.GetDeviceCaps(dc, index);



   /// <summary>Calls DeleteDC.</summary>
   public bool DeleteDC(IntPtr dc) => Native.DeleteDC(dc);



   /// <summary>Calls StartDoc with a DOCINFO that carries its own size and the document name and nothing else.</summary>
   public int StartDoc(IntPtr dc, string documentName)
   {
      Native.DocInfo info = new() { Size = Marshal.SizeOf<Native.DocInfo>(), DocumentName = documentName };
      return Native.StartDoc(dc, ref info);
   }



   /// <summary>Calls StartPage.</summary>
   public int StartPage(IntPtr dc) => Native.StartPage(dc);



   /// <summary>Calls SetStretchBltMode.</summary>
   public int SetStretchBltMode(IntPtr dc, int mode) => Native.SetStretchBltMode(dc, mode);



   /// <summary>Calls StretchDIBits with a 40-byte BITMAPINFOHEADER filled from the given header and no compression.</summary>
   public int StretchDIBits(IntPtr dc, int x, int y, int width, int height, int sourceX, int sourceY, int sourceWidth, int sourceHeight, byte[] bits, GdiBitmapHeader header, uint usage, uint operation)
   {
      Native.BitmapInfo bitmap = new() { HeaderSize = 40, Width = header.Width, Height = header.Height, Planes = header.Planes, BitCount = header.BitCount };
      return Native.StretchDIBits(dc, x, y, width, height, sourceX, sourceY, sourceWidth, sourceHeight, bits, ref bitmap, usage, operation);
   }



   /// <summary>Calls EndPage.</summary>
   public int EndPage(IntPtr dc) => Native.EndPage(dc);



   /// <summary>Calls EndDoc.</summary>
   public int EndDoc(IntPtr dc) => Native.EndDoc(dc);



   /// <summary>Calls AbortDoc.</summary>
   public int AbortDoc(IntPtr dc) => Native.AbortDoc(dc);



   /// <summary>
   /// The Win32 GDI entry points this adapter calls. Every declaration mirrors the documented native signature exactly;
   /// the layouts below are marshaled by the runtime, so field order and types must not change.
   /// </summary>
   private static class Native
   {
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
