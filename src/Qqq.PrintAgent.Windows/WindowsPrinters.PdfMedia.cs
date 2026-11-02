/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Prepares job-local PDF settings through the driver while preserving the queue-default fingerprint and leaving persistent settings untouched.
/// </summary>
public sealed partial class WindowsPrinters
{
   /// <summary>
   /// Normalizes a selected PDF job and resolves the returned driver settings again, refusing any geometry or DPI change.
   /// </summary>
   private static byte[] PreparePdfMode(PrinterInfo printer, QueueDefaultMedia media)
      => NormalizePdfMode(media.DevMode, media.Profile!, mode => MergeDriverMode(printer.Name, mode), mode =>
      {
         var pinned = GCHandle.Alloc(mode, GCHandleType.Pinned);
         try
         {
            IntPtr pointer = pinned.AddrOfPinnedObject();
            (int width, int height) = ResolvePaperDimensions(printer.Name, printer.PortName, mode, pointer);
            (int dpiX, int dpiY) = ResolveResolution(printer.Name, mode, pointer);
            return BuildMediaProfile(printer.PrinterId, printer.DriverName, mode, width, height, dpiX, dpiY);
         }
         finally { pinned.Free(); }
      });



   /// <summary>
   /// Clones captured defaults, overrides only supported copy/scale fields, and verifies driver output without altering the selection fingerprint.
   /// </summary>
   internal static byte[] NormalizePdfMode(byte[] captured, PrinterMediaProfile selected,
      Func<byte[], byte[]> normalize, Func<byte[], PrinterMediaProfile?> resolve)
   {
      byte[] input = captured.ToArray();
      uint fields = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(72));
      uint overrides = fields & 0x110;
      if((overrides & 0x10) != 0)
      { BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(84), 100); }
      if((overrides & 0x100) != 0)
      { BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(86), 1); }
      byte[] output = normalize(input);
      RequirePdfDefaults(output);
      if((BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(72)) & 0x110) != overrides)
      {
         throw new PrinterMediaMismatchException("The driver changed support for job-level copies or scale.");
      }
      PrinterMediaProfile? effective = resolve(output);
      if(effective is null || effective.WidthMicrons != selected.WidthMicrons || effective.HeightMicrons != selected.HeightMicrons
         || effective.DpiX != selected.DpiX || effective.DpiY != selected.DpiY)
      {
         throw new PrinterMediaMismatchException("The driver changed selected media while applying job-level copies and scale.");
      }
      return output;
   }



   /// <summary>
   /// Lets DocumentProperties reconcile private settings in bounded buffers using input/output flags only, with no prompt or queue update.
   /// </summary>
   private static byte[] MergeDriverMode(string printerName, byte[] input)
   {
      Check(Native.OpenPrinter(printerName, out IntPtr printer, IntPtr.Zero));
      try
      {
         int required = Native.DocumentProperties(IntPtr.Zero, printer, printerName, IntPtr.Zero, IntPtr.Zero, 0);
         if(required is < 102 or > 1_048_576)
         {
            throw new PrinterMediaMismatchException("The driver did not provide a bounded document-settings buffer.");
         }
         byte[] output = new byte[required];
         var source = GCHandle.Alloc(input, GCHandleType.Pinned);
         var destination = GCHandle.Alloc(output, GCHandleType.Pinned);
         try
         {
            // DM_IN_BUFFER | DM_OUT_BUFFER only: no prompt and no queue-default update.
            if(Native.DocumentProperties(IntPtr.Zero, printer, printerName, destination.AddrOfPinnedObject(), source.AddrOfPinnedObject(), 10) != 1)
            {
               throw new PrinterMediaMismatchException("The driver refused job-level document settings.");
            }
         }
         finally { destination.Free(); source.Free(); }
         int size = BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(68));
         int extra = BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(70));
         if(size < 102 || size + extra > output.Length)
         {
            throw new PrinterMediaMismatchException("The driver returned incomplete document settings.");
         }
         return output[..(size + extra)];
      }
      finally { _ = Native.ClosePrinter(printer); }
   }



   /// <summary>
   /// Contains the driver-normalization call used to create application-local DEVMODE settings.
   /// </summary>
   private static partial class Native
   {
      /// <summary>
      /// Queries required buffer size or merges job settings; callers deliberately omit prompt and persistent-update flags.
      /// </summary>
      [DllImport("winspool.drv", EntryPoint = "DocumentPropertiesW", CharSet = CharSet.Unicode)]
      internal static extern int DocumentProperties(IntPtr window, IntPtr printer, string device, IntPtr output, IntPtr input, uint mode);
   }
}
