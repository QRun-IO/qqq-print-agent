/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The real Windows behind <see cref="IPrinterSettings"/>: each member makes one call into winspool.drv, with the byte
/// arrays it is given pinned or copied into unmanaged memory for the length of that call, and reports what the call
/// returned. It decides nothing about settings.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Calls GetPrinter, DeviceCapabilities and DocumentProperties in winspool.drv. A test cannot run these without an installed Windows print queue and its driver on this computer, and what they answer depends on that driver. No rule lives here: which level and capability is asked for, every bound on a size or a count, how the returned bytes are read, which mode flags are passed and whether the driver's answer is accepted are decided in WindowsPrinters, which is tested with a fake of IPrinterSettings. The one branch here copies a GetPrinter buffer out only when Windows filled it.")]
internal sealed class WindowsPrinterSettings : IPrinterSettings
{
   /// <summary>Calls GetPrinter with no buffer, which only reports the size the details need.</summary>
   public bool GetPrinterSize(IntPtr printer, uint level, out uint needed) => GetPrinter(printer, level, IntPtr.Zero, 0, out needed);



   /// <summary>
   /// Calls GetPrinter into an unmanaged buffer of the given size and copies the whole buffer out before it is freed,
   /// together with the address it had. The error GetPrinter left is put back after the buffer is freed, so the caller
   /// reads the error of GetPrinter and not of the cleanup.
   /// </summary>
   public bool GetPrinter(IntPtr printer, uint level, uint size, out byte[] buffer, out long address)
   {
      int error = 0;
      IntPtr memory = Marshal.AllocHGlobal(checked((int)size));
      try
      {
         bool read = GetPrinter(printer, level, memory, size, out _);
         error = Marshal.GetLastPInvokeError();
         address = memory.ToInt64();
         buffer = read ? new byte[size] : [];
         Marshal.Copy(memory, buffer, 0, buffer.Length);
         return read;
      }
      finally
      {
         Marshal.FreeHGlobal(memory);
         Marshal.SetLastPInvokeError(error);
      }
   }



   /// <summary>Calls DeviceCapabilities with no output buffer and the settings pinned, which only reports the number of entries.</summary>
   public int DeviceCapabilities(string device, string port, ushort capability, byte[] devMode)
   {
      var pinned = GCHandle.Alloc(devMode, GCHandleType.Pinned);
      try
      { return DeviceCapabilities(device, port, capability, IntPtr.Zero, pinned.AddrOfPinnedObject()); }
      finally { pinned.Free(); }
   }



   /// <summary>Calls DeviceCapabilities into an unmanaged buffer of the given size with the settings pinned, and copies the buffer out before it is freed.</summary>
   public int DeviceCapabilities(string device, string port, ushort capability, int outputBytes, byte[] devMode, out byte[] output)
   {
      IntPtr memory = Marshal.AllocHGlobal(outputBytes);
      var pinned = GCHandle.Alloc(devMode, GCHandleType.Pinned);
      try
      {
         int entries = DeviceCapabilities(device, port, capability, memory, pinned.AddrOfPinnedObject());
         output = new byte[outputBytes];
         Marshal.Copy(memory, output, 0, outputBytes);
         return entries;
      }
      finally
      {
         pinned.Free();
         Marshal.FreeHGlobal(memory);
      }
   }



   /// <summary>Calls DocumentProperties with no window, no buffers and mode 0, which only reports the size of the driver's settings.</summary>
   public int DocumentPropertiesSize(IntPtr printer, string device) => DocumentProperties(IntPtr.Zero, printer, device, IntPtr.Zero, IntPtr.Zero, 0);



   /// <summary>Calls DocumentProperties with no window and both arrays pinned for the length of the call.</summary>
   public int DocumentProperties(IntPtr printer, string device, byte[] output, byte[] input, uint mode)
   {
      var source = GCHandle.Alloc(input, GCHandleType.Pinned);
      var destination = GCHandle.Alloc(output, GCHandleType.Pinned);
      try
      { return DocumentProperties(IntPtr.Zero, printer, device, destination.AddrOfPinnedObject(), source.AddrOfPinnedObject(), mode); }
      finally
      {
         destination.Free();
         source.Free();
      }
   }



   /// <summary>
   /// Reads printer information into a caller-sized buffer; level eight supplies machine-wide DEVMODE defaults.
   /// </summary>
   [DllImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool GetPrinter(IntPtr printer, uint level, IntPtr buffer, uint size, out uint needed);



   /// <summary>
   /// Queries driver capabilities against the supplied DEVMODE so paper IDs and sizes refer to the same captured configuration.
   /// </summary>
   [DllImport("winspool.drv", EntryPoint = "DeviceCapabilitiesW", CharSet = CharSet.Unicode)]
   private static extern int DeviceCapabilities(string device, string port, ushort capability, IntPtr output, IntPtr devMode);



   /// <summary>
   /// Queries required buffer size or merges job settings; callers deliberately omit prompt and persistent-update flags.
   /// </summary>
   [DllImport("winspool.drv", EntryPoint = "DocumentPropertiesW", CharSet = CharSet.Unicode)]
   private static extern int DocumentProperties(IntPtr window, IntPtr printer, string device, IntPtr output, IntPtr input, uint mode);
}
