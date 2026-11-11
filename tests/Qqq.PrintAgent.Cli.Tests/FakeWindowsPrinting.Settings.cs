/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The printer settings part of the fake: the level 8 machine defaults, the driver's paper lists and its merge of
/// document settings, each answered from the <see cref="FakeQueue"/> the call names.
/// </summary>
internal sealed partial class FakeWindowsPrinting
{
   /// <summary>The address the fake says its level 8 answer was written at; the pointer inside the answer is relative to it.</summary>
   internal const long AnswerAddress = 0x7ff0_0000_1000;



   /// <summary>The settings each paper capability call was given, in order.</summary>
   internal List<byte[]> CapabilityModes { get; } = [];



   /// <summary>The settings the driver was asked to merge, in order.</summary>
   internal List<byte[]> MergeInputs { get; } = [];



   /// <summary>Answers the sizing call the way Windows does: false, error 122 and the size of a pointer plus the machine defaults (zero for a queue without any).</summary>
   public bool GetPrinterSize(IntPtr printer, uint level, out uint needed)
   {
      StaleLastError.Leave();
      Calls.Add($"GetPrinterSize({Handle(printer)}, {level})");
      FakeQueue queue = Printer(printer);
      needed = queue.ReportedDefaultsSize ?? (queue.MachineDefaults is null ? 0 : (uint)(IntPtr.Size + queue.MachineDefaults.Length));
      Marshal.SetLastPInvokeError(122);
      return false;
   }



   /// <summary>
   /// Builds a level 8 answer of the asked size: a pointer to the settings in its first field and the settings themselves
   /// at that place, as far as they fit. The queue can make the read fail, leave the pointer null or point it elsewhere.
   /// </summary>
   public bool GetPrinter(IntPtr printer, uint level, uint size, out byte[] buffer, out long address)
   {
      StaleLastError.Leave();
      Calls.Add($"GetPrinter({Handle(printer)}, {level}, {size})");
      FakeQueue queue = Printer(printer);
      if(queue.GetPrinterError is { } error)
      {
         buffer = [];
         address = 0;
         Marshal.SetLastPInvokeError(error);
         return false;
      }
      buffer = new byte[size];
      address = AnswerAddress;
      int at = (int)Math.Clamp(queue.DevModeOffset, 0, size);
      byte[] defaults = queue.MachineDefaults ?? [];
      defaults.AsSpan(0, Math.Min(defaults.Length, buffer.Length - at)).CopyTo(buffer.AsSpan(at));
      BinaryPrimitives.WriteInt64LittleEndian(buffer, queue.NullDevModePointer ? 0 : address + queue.DevModeOffset);
      return true;
   }



   /// <summary>Answers how many entries a paper capability has: the length of the queue's list, or the answer a test put in its place.</summary>
   public int DeviceCapabilities(string device, string port, ushort capability, byte[] devMode)
   {
      Calls.Add($"DeviceCapabilities({device}, {port}, {capability}, count)");
      Ask(device);
      CapabilityModes.Add(devMode.ToArray());
      FakeQueue queue = Find(device) ?? throw new InvalidOperationException("A capability was asked of a queue that does not exist.");
      return queue.CountAnswers.TryGetValue(capability, out int answer) ? answer : Entries(queue, capability);
   }



   /// <summary>
   /// Writes a paper capability's entries into a buffer of the asked size, as far as they fit: identifiers as 16-bit
   /// values, sizes as pairs of 32-bit values. It reports the number of entries, or the answer a test put in its place.
   /// </summary>
   public int DeviceCapabilities(string device, string port, ushort capability, int outputBytes, byte[] devMode, out byte[] output)
   {
      Calls.Add($"DeviceCapabilities({device}, {port}, {capability}, {outputBytes} bytes)");
      Ask(device);
      CapabilityModes.Add(devMode.ToArray());
      FakeQueue queue = Find(device) ?? throw new InvalidOperationException("A capability was asked of a queue that does not exist.");
      output = new byte[outputBytes];
      for(int index = 0; capability == 2 && index < queue.PaperIds.Length && (index + 1) * 2 <= outputBytes; index++)
      {
         BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(index * 2), queue.PaperIds[index]);
      }
      for(int index = 0; capability == 3 && index < queue.PaperSizes.Length && (index + 1) * 8 <= outputBytes; index++)
      {
         BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(index * 8), queue.PaperSizes[index].Width);
         BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(index * 8 + 4), queue.PaperSizes[index].Height);
      }
      return queue.ReadAnswers.TryGetValue(capability, out int answer) ? answer : Entries(queue, capability);
   }



   /// <summary>Answers the size of the driver's document settings: the size of the machine defaults, or the size a test put in its place.</summary>
   public int DocumentPropertiesSize(IntPtr printer, string device)
   {
      Calls.Add($"DocumentPropertiesSize({Handle(printer)}, {device})");
      FakeQueue queue = Printer(printer);
      return queue.SettingsSize ?? queue.MachineDefaults?.Length ?? 0;
   }



   /// <summary>Lets the queue's driver merge the input settings and copies what it made of them into the output, as far as it fits.</summary>
   public int DocumentProperties(IntPtr printer, string device, byte[] output, byte[] input, uint mode)
   {
      Calls.Add($"DocumentProperties({Handle(printer)}, {device}, {output.Length} bytes, {input.Length} bytes, {mode})");
      FakeQueue queue = Printer(printer);
      MergeInputs.Add(input.ToArray());
      byte[] merged = queue.Merge(input.ToArray());
      merged.AsSpan(0, Math.Min(merged.Length, output.Length)).CopyTo(output);
      return queue.MergeResult;
   }



   /// <summary>The number of entries the queue really has for a paper capability; any other capability is not one the rules may ask for.</summary>
   private static int Entries(FakeQueue queue, ushort capability) => capability switch
   {
      2 => queue.PaperIds.Length,
      3 => queue.PaperSizes.Length,
      _ => throw new InvalidOperationException($"Capability {capability} is not one the rules may ask for."),
   };
}
