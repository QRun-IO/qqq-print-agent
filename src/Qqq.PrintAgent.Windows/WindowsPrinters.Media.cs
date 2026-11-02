/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Reads machine queue defaults and verifies selected media before native submission, without inferring loaded stock.
/// </summary>
public sealed partial class WindowsPrinters
{
   /// <summary>
   /// Keeps the complete driver bytes beside their resolved profile so validation and submission use the same captured defaults.
   /// </summary>
   private sealed record QueueDefaultMedia(byte[] DevMode, PrinterMediaProfile? Profile);



   /// <summary>
   /// Resolves bounded machine defaults into a selectable profile; unreadable queues remain discoverable without a media claim.
   /// </summary>
   private static QueueDefaultMedia? ReadQueueMedia(string id, string name, string driver, string port)
   {
      try
      {
         byte[]? mode = ReadMachineDevMode(name);
         if(mode is null)
         { return null; }
         var pinned = GCHandle.Alloc(mode, GCHandleType.Pinned);
         try
         {
            IntPtr pointer = pinned.AddrOfPinnedObject();
            (int width, int height) = ResolvePaperDimensions(name, port, mode, pointer);
            (int dpiX, int dpiY) = ResolveResolution(name, mode, pointer);
            return new QueueDefaultMedia(mode, BuildMediaProfile(id, driver, mode, width, height, dpiX, dpiY));
         }
         finally { pinned.Free(); }
      }
      catch(Win32Exception)
      {
         // A queue without readable machine defaults has no selectable media profile.
         return null;
      }
   }



   /// <summary>
   /// Copies level-eight machine defaults only after checking native pointer and length bounds, avoiding per-user preferences.
   /// </summary>
   private static byte[]? ReadMachineDevMode(string name)
   {
      Check(Native.OpenPrinter(name, out IntPtr printer, IntPtr.Zero));
      try
      {
         _ = Native.GetPrinter(printer, 8, IntPtr.Zero, 0, out uint needed);
         if(needed < IntPtr.Size + 102 || needed > 1_048_576)
         { return null; }
         IntPtr buffer = Marshal.AllocHGlobal(checked((int)needed));
         try
         {
            Check(Native.GetPrinter(printer, 8, buffer, needed, out _));
            IntPtr mode = Marshal.ReadIntPtr(buffer);
            long offset = mode.ToInt64() - buffer.ToInt64();
            if(mode == IntPtr.Zero || offset < IntPtr.Size || offset > needed - 102)
            { return null; }
            int size = (ushort)Marshal.ReadInt16(mode, 68);
            int extra = (ushort)Marshal.ReadInt16(mode, 70);
            if(size < 102 || size + extra > needed - offset)
            { return null; }
            byte[] bytes = new byte[size + extra];
            Marshal.Copy(mode, bytes, 0, bytes.Length);
            return bytes;
         }
         finally { Marshal.FreeHGlobal(buffer); }
      }
      finally { _ = Native.ClosePrinter(printer); }
   }



   /// <summary>
   /// Uses both explicit form dimensions or an unambiguous driver paper-ID mapping, because partial dimensions are not reliable stock data.
   /// </summary>
   private static (int Width, int Height) ResolvePaperDimensions(string name, string port, byte[] mode, IntPtr pointer)
   {
      uint fields = BinaryPrimitives.ReadUInt32LittleEndian(mode.AsSpan(72));
      if((fields & 12) != 0)
      {
         // Both explicit fields are required; a half-specified custom form is ambiguous.
         return ReadExplicitDimensions(mode);
      }
      if((fields & 2) == 0)
      { return (0, 0); }
      return ReadDriverPaperSize(name, port, pointer, ReadModeShort(mode, 78));
   }



   /// <summary>
   /// Pairs the driver's paper identifiers and size arrays and accepts exactly one bounded match, rather than guessing a standard form.
   /// </summary>
   private static (int Width, int Height) ReadDriverPaperSize(string name, string port, IntPtr mode, short paperId)
   {
      int count = Native.DeviceCapabilities(name, port, 2, IntPtr.Zero, mode);
      if(count is < 1 or > ushort.MaxValue || Native.DeviceCapabilities(name, port, 3, IntPtr.Zero, mode) != count)
      { return (0, 0); }
      IntPtr identifiers = Marshal.AllocHGlobal(count * sizeof(short));
      IntPtr dimensions = Marshal.AllocHGlobal(count * 2 * sizeof(int));
      try
      {
         if(Native.DeviceCapabilities(name, port, 2, identifiers, mode) != count || Native.DeviceCapabilities(name, port, 3, dimensions, mode) != count)
         { return (0, 0); }
         List<(int Width, int Height)> matches = [];
         for(int index = 0; index < count; index++)
         {
            if(Marshal.ReadInt16(identifiers, index * 2) == paperId)
            {
               int width = Marshal.ReadInt32(dimensions, index * 8);
               int height = Marshal.ReadInt32(dimensions, index * 8 + 4);
               if(width is < 1 or > 20_000 || height is < 1 or > 20_000)
               { return (0, 0); }
               matches.Add((width * 100, height * 100));
            }
         }
         return matches.Count == 1 ? matches[0] : (0, 0);
      }
      finally { Marshal.FreeHGlobal(identifiers); Marshal.FreeHGlobal(dimensions); }
   }



   /// <summary>
   /// Uses positive explicit DPI or a DC built from the captured defaults; symbolic quality values are not treated as resolution.
   /// </summary>
   private static (int X, int Y) ResolveResolution(string name, byte[] mode, IntPtr pointer)
   {
      uint fields = BinaryPrimitives.ReadUInt32LittleEndian(mode.AsSpan(72));
      if((fields & 0x2400) == 0x2400 && ReadModeShort(mode, 90) > 0 && ReadModeShort(mode, 96) > 0)
      { return (ReadModeShort(mode, 90), ReadModeShort(mode, 96)); }
      // Symbolic quality values are not DPI. Ask this driver with this exact machine DEVMODE.
      IntPtr dc = Native.CreateDC("WINSPOOL", name, null, pointer);
      if(dc == IntPtr.Zero)
      { return (0, 0); }
      try
      { return (Native.GetDeviceCaps(dc, 88), Native.GetDeviceCaps(dc, 90)); }
      finally { _ = Native.DeleteDC(dc); }
   }



   /// <summary>
   /// Validates dimensions, orientation and resolution, then fingerprints all public/private defaults so driver changes invalidate stale selections.
   /// </summary>
   internal static PrinterMediaProfile? BuildMediaProfile(string printerId, string driver, byte[] mode, int width, int height, int dpiX, int dpiY)
   {
      if(mode.Length < 102 || BinaryPrimitives.ReadUInt16LittleEndian(mode.AsSpan(68)) < 102
         || BinaryPrimitives.ReadUInt16LittleEndian(mode.AsSpan(68)) + BinaryPrimitives.ReadUInt16LittleEndian(mode.AsSpan(70)) != mode.Length
         || width is < 1 or > 2_000_000 || height is < 1 or > 2_000_000 || dpiX is < 1 or > 9600 || dpiY is < 1 or > 9600)
      { return null; }
      uint fields = BinaryPrimitives.ReadUInt32LittleEndian(mode.AsSpan(72));
      if((fields & 1) == 0 || ReadModeShort(mode, 76) is not (1 or 2))
      { return null; }
      if(ReadModeShort(mode, 76) == 2)
      { (width, height) = (height, width); }
      string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(printerId + ":machine-default"))).ToLowerInvariant()[..24];
      string canonical = string.Create(CultureInfo.InvariantCulture, $"{printerId}\n{driver}\n{width}\n{height}\n{dpiX}\n{dpiY}\n");
      using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      hash.AppendData(Encoding.UTF8.GetBytes(canonical));
      hash.AppendData(mode);
      string revision = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
      string label = string.Create(CultureInfo.InvariantCulture, $"Windows queue default ({width / 1000m} × {height / 1000m} mm, {dpiX} × {dpiY} DPI)");
      return new PrinterMediaProfile(id, revision, label, width, height, dpiX, dpiY);
   }



   /// <summary>
   /// Reads a validated DEVMODE short at its native little-endian offset without interpreting driver-private bytes.
   /// </summary>
   private static short ReadModeShort(byte[] mode, int offset) => BinaryPrimitives.ReadInt16LittleEndian(mode.AsSpan(offset));



   /// <summary>
   /// Converts complete DEVMODE form dimensions from tenths of a millimeter to microns; missing fields return unknown geometry.
   /// </summary>
   internal static (int Width, int Height) ReadExplicitDimensions(byte[] mode)
      => mode.Length >= 84 && (BinaryPrimitives.ReadUInt32LittleEndian(mode.AsSpan(72)) & 12) == 12
         ? (ReadModeShort(mode, 82) * 100, ReadModeShort(mode, 80) * 100) : (0, 0);



   /// <summary>
   /// Re-reads and matches selected defaults at the native submission boundary, while leaving legacy unselected jobs on their existing path.
   /// </summary>
   private static QueueDefaultMedia? RequireCurrentMedia(PrinterInfo printer, PrintJob job)
   {
      if(job.Options.MediaProfile is null)
      { return null; }
      QueueDefaultMedia? media = ReadQueueMedia(printer.PrinterId, printer.Name, printer.DriverName, printer.PortName);
      PrinterMediaProfile.RequireMatch(job.Options, media?.Profile is null ? [] : [media.Profile]);
      return media;
   }



   /// <summary>
   /// Passes the pinned captured or normalized job settings to GDI so selected jobs do not silently request current-user defaults.
   /// </summary>
   private static IntPtr CreateDocumentContext(string printerName, byte[]? mode)
      => WithPinnedMode(mode, pointer => Native.CreateDC("WINSPOOL", printerName, null, pointer));



   /// <summary>
   /// Checks active overrides and the effective DC's physical dimensions/DPI before invoking StartDoc, so substituted defaults cannot open a spool document.
   /// </summary>
   internal static int StartValidatedPdfDocument(byte[]? mode, PrinterMediaProfile? profile, Func<int, int> deviceCaps, Func<int> startDocument)
   {
      if(profile is not null)
      {
         RequirePdfDefaults(mode);
         int dpiX = deviceCaps(88);
         int dpiY = deviceCaps(90);
         if(dpiX != profile.DpiX || dpiY != profile.DpiY
            || !MatchesPhysicalDimension(deviceCaps(110), dpiX, profile.WidthMicrons)
            || !MatchesPhysicalDimension(deviceCaps(111), dpiY, profile.HeightMicrons))
         {
            throw new PrinterMediaMismatchException("The printer device context does not match the selected media dimensions and resolution.");
         }
      }
      return startDocument();
   }



   /// <summary>
   /// Rejects active driver copy or scale settings that would multiply or resize the renderer's output; unsupported fields remain ignored.
   /// </summary>
   private static void RequirePdfDefaults(byte[]? mode)
   {
      if(mode is null || mode.Length < 102)
      {
         throw new PrinterMediaMismatchException("The selected PDF media has no complete machine defaults.");
      }
      uint fields = BinaryPrimitives.ReadUInt32LittleEndian(mode.AsSpan(72));
      // The renderer owns copies and scaling. Do not patch vendor-private DEVMODE data.
      if(((fields & 0x100) != 0 && ReadModeShort(mode, 86) != 1)
         || ((fields & 0x10) != 0 && ReadModeShort(mode, 84) != 100))
      {
         throw new PrinterMediaMismatchException("Selected PDF media requires Windows queue defaults of one copy and 100 percent scale.");
      }
   }



   /// <summary>
   /// Compares physical pixels with selected microns using only device-pixel and form-unit rounding tolerance, never printable-area dimensions.
   /// </summary>
   private static bool MatchesPhysicalDimension(int pixels, int dpi, int microns)
   {
      if(pixels <= 0 || dpi <= 0)
      { return false; }
      // Allow one device pixel plus half the DEVMODE form unit (0.1 mm).
      double pixelMicrons = 25_400d / dpi;
      return Math.Abs(pixels * pixelMicrons - microns) <= pixelMicrons + 50;
   }



   /// <summary>
   /// Pins the complete settings buffer for the synchronous native call and always releases it; null is reserved for the legacy path.
   /// </summary>
   internal static IntPtr WithPinnedMode(byte[]? mode, Func<IntPtr, IntPtr> create)
   {
      if(mode is null)
      { return create(IntPtr.Zero); }
      var pinned = GCHandle.Alloc(mode, GCHandleType.Pinned);
      try
      { return create(pinned.AddrOfPinnedObject()); }
      finally { pinned.Free(); }
   }



   /// <summary>
   /// Contains read-only Winspool entry points used to obtain machine defaults and driver form capabilities.
   /// </summary>
   private static partial class Native
   {
      /// <summary>
      /// Reads printer information into a caller-sized buffer; level eight supplies machine-wide DEVMODE defaults.
      /// </summary>
      [DllImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true)]
      [return: MarshalAs(UnmanagedType.Bool)]
      internal static extern bool GetPrinter(IntPtr printer, uint level, IntPtr buffer, uint size, out uint needed);



      /// <summary>
      /// Queries driver capabilities against the supplied DEVMODE so paper IDs and sizes refer to the same captured configuration.
      /// </summary>
      [DllImport("winspool.drv", EntryPoint = "DeviceCapabilitiesW", CharSet = CharSet.Unicode)]
      internal static extern int DeviceCapabilities(string device, string port, ushort capability, IntPtr output, IntPtr devMode);
   }
}
