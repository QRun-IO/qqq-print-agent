/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The Windows calls that read a queue's settings and let its driver reconcile a job's settings: <c>GetPrinter</c>,
/// <c>DeviceCapabilities</c> and <c>DocumentProperties</c>. Each member is one Windows call and reports what Windows
/// answered; a <c>GetPrinter</c> call that fails returns false and leaves its Windows error as the thread's last error
/// (<c>Marshal.GetLastWin32Error</c>), exactly as the native call does. Which settings are read, the bounds on every
/// answer, what counts as usable paper and resolution, and whether a driver's answer is accepted are decided by
/// <see cref="WindowsPrinters"/>.
/// </summary>
internal interface IPrinterSettings
{
   /// <summary>
   /// Asks how many bytes a queue's details need at the given level (<c>GetPrinter</c> with no buffer). Windows answers
   /// this sizing call with false and a size.
   /// </summary>
   bool GetPrinterSize(IntPtr printer, uint level, out uint needed);



   /// <summary>
   /// Reads a queue's details at the given level into a buffer of the given size (<c>GetPrinter</c>) and reports the bytes
   /// Windows wrote together with the address they were written at, so a pointer stored inside them can be turned into an
   /// offset. The buffer is empty when the call failed.
   /// </summary>
   bool GetPrinter(IntPtr printer, uint level, uint size, out byte[] buffer, out long address);



   /// <summary>
   /// Asks the driver how many entries one capability has for the given settings (<c>DeviceCapabilities</c> with no output
   /// buffer), such as 2 for paper identifiers or 3 for paper sizes. A driver that cannot answer returns -1.
   /// </summary>
   int DeviceCapabilities(string device, string port, ushort capability, byte[] devMode);



   /// <summary>
   /// Reads one capability's entries for the given settings into a buffer of the given number of bytes
   /// (<c>DeviceCapabilities</c>) and reports the number of entries the driver says it wrote and the buffer's bytes.
   /// </summary>
   int DeviceCapabilities(string device, string port, ushort capability, int outputBytes, byte[] devMode, out byte[] output);



   /// <summary>
   /// Asks how many bytes the driver's complete document settings need (<c>DocumentProperties</c> with no buffers and no
   /// mode flags). Nothing is shown and nothing is changed.
   /// </summary>
   int DocumentPropertiesSize(IntPtr printer, string device);



   /// <summary>
   /// Lets the driver merge the input settings into the output buffer (<c>DocumentProperties</c>) under the given mode
   /// flags, with no owner window. The driver returns 1 when it accepted the settings.
   /// </summary>
   int DocumentProperties(IntPtr printer, string device, byte[] output, byte[] input, uint mode);
}
