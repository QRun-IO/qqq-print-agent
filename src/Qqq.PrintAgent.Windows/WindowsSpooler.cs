/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The real Windows spooler behind <see cref="ISpooler"/>: each member makes one call into winspool.drv and reports what
/// it returned. It decides nothing about queues or documents.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Calls EnumPrinters, OpenPrinter, StartDocPrinter, WritePrinter, EndDocPrinter, AbortPrinter and ClosePrinter in winspool.drv. A test cannot run these without listing this computer's print queues or sending a document to one of them, which tests must never do. No rule lives here: which queues are listed (the flags) and which of them may print unattended, the queue identifier, the document name and data type, how the bytes are split and repeated per copy, the check of each written count, and when a document is ended, aborted or its queue closed are decided in WindowsPrinters, which is tested with a fake of ISpooler. Fixed here, because each is the shape of one call and not a rule: EnumPrinters is always asked at level 2 with no server name, and its sizing call passes no buffer and size 0; OpenPrinter passes no defaults structure; StartDocPrinter is called at level 1 with no output file. The one branch here reads queue records only when Windows filled the buffer, because the pointers in an unfilled buffer are not valid. After that buffer is freed, the error EnumPrinters left is put back as the thread's last error, so the caller reads the error of EnumPrinters and not of the cleanup.")]
internal sealed class WindowsSpooler : ISpooler
{
   /// <summary>Calls EnumPrinters at level 2 with no server name and no buffer, which only reports the size the list needs.</summary>
   public bool EnumPrintersSize(uint flags, out uint needed) => Native.EnumPrinters(flags, null, 2, IntPtr.Zero, 0, out needed, out _);



   /// <summary>
   /// Calls EnumPrinters at level 2 into an unmanaged buffer of the given size and copies each queue's name, driver, port
   /// and status out of it before the buffer is freed. A failed call leaves the buffer unfilled, so nothing is read from
   /// it. The error EnumPrinters left is put back after the buffer is freed, so the caller reads the error of
   /// EnumPrinters and not of the cleanup.
   /// </summary>
   public bool EnumPrinters(uint flags, int size, out IReadOnlyList<SpoolerQueue> queues)
   {
      int error = 0;
      List<SpoolerQueue> listed = [];
      queues = listed;
      IntPtr memory = Marshal.AllocHGlobal(size);
      try
      {
         bool read = Native.EnumPrinters(flags, null, 2, memory, (uint)size, out _, out uint count);
         error = Marshal.GetLastPInvokeError();
         for(int i = 0; read && i < count; i++)
         {
            Native.PrinterInfo2 info = Marshal.PtrToStructure<Native.PrinterInfo2>(memory + i * Marshal.SizeOf<Native.PrinterInfo2>());
            listed.Add(new SpoolerQueue(Marshal.PtrToStringUni(info.PrinterName), Marshal.PtrToStringUni(info.DriverName), Marshal.PtrToStringUni(info.PortName), info.Status));
         }
         return read;
      }
      finally
      {
         Marshal.FreeHGlobal(memory);
         Marshal.SetLastPInvokeError(error);
      }
   }



   /// <summary>Opens the queue with no defaults structure, so the handle has the caller's default access.</summary>
   public bool OpenPrinter(string name, out IntPtr printer) => Native.OpenPrinter(name, out printer, IntPtr.Zero);



   /// <summary>Calls StartDocPrinter at level 1 with the given name and data type and no output file.</summary>
   public uint StartDocPrinter(IntPtr printer, string documentName, string dataType)
   {
      Native.DocInfo1 info = new() { DocumentName = documentName, DataType = dataType };
      return Native.StartDocPrinter(printer, 1, ref info);
   }



   /// <summary>Calls WritePrinter with the array and count as given.</summary>
   public bool WritePrinter(IntPtr printer, byte[] bytes, int count, out int written) => Native.WritePrinter(printer, bytes, count, out written);



   /// <summary>Calls EndDocPrinter.</summary>
   public bool EndDocPrinter(IntPtr printer) => Native.EndDocPrinter(printer);



   /// <summary>Calls AbortPrinter.</summary>
   public bool AbortPrinter(IntPtr printer) => Native.AbortPrinter(printer);



   /// <summary>Calls ClosePrinter.</summary>
   public bool ClosePrinter(IntPtr printer) => Native.ClosePrinter(printer);



   /// <summary>
   /// The Win32 spooler entry points this adapter calls. Every declaration mirrors the documented native signature
   /// exactly; the layouts below are marshaled by the runtime, so field order and types must not change.
   /// </summary>
   private static class Native
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
   }
}
