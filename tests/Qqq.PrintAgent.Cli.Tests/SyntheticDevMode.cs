/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Buffers.Binary;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Builds the driver settings (DEVMODE) the fake queues hand out. The bytes are made up: a 220-byte public part and four
/// private bytes, with only the fields the printing rules read filled in. No driver was asked for them.
/// </summary>
internal static class SyntheticDevMode
{
   /// <summary>The fields flag for orientation.</summary>
   internal const uint Orientation = 0x1;

   /// <summary>The fields flag for a paper identifier.</summary>
   internal const uint PaperSize = 0x2;

   /// <summary>The fields flags for an explicit paper length and width.</summary>
   internal const uint PaperDimensions = 0x4 | 0x8;

   /// <summary>The fields flag for driver scaling.</summary>
   internal const uint Scale = 0x10;

   /// <summary>The fields flag for driver copies.</summary>
   internal const uint Copies = 0x100;

   /// <summary>The fields flags for an explicit horizontal and vertical resolution.</summary>
   internal const uint Resolution = 0x400 | 0x2000;



   /// <summary>
   /// A portrait label 101.6 mm wide and the given length (in tenths of a millimeter) long, with explicit dimensions and no
   /// explicit resolution, so the resolution has to come from a device context.
   /// </summary>
   internal static byte[] Label(short length = 1524)
   {
      byte[] mode = new byte[224];
      BinaryPrimitives.WriteUInt16LittleEndian(mode.AsSpan(68), 220);
      BinaryPrimitives.WriteUInt16LittleEndian(mode.AsSpan(70), 4);
      BinaryPrimitives.WriteUInt32LittleEndian(mode.AsSpan(72), Orientation | PaperDimensions);
      BinaryPrimitives.WriteInt16LittleEndian(mode.AsSpan(76), 1);
      BinaryPrimitives.WriteInt16LittleEndian(mode.AsSpan(80), length);
      BinaryPrimitives.WriteInt16LittleEndian(mode.AsSpan(82), 1016);
      mode[^1] = 0x5a;
      return mode;
   }



   /// <summary>Replaces the fields flags and returns the same settings, so calls can be chained.</summary>
   internal static byte[] WithFields(this byte[] mode, uint fields)
   {
      BinaryPrimitives.WriteUInt32LittleEndian(mode.AsSpan(72), fields);
      return mode;
   }



   /// <summary>Writes one 16-bit setting at its offset and returns the same settings, so calls can be chained.</summary>
   internal static byte[] WithShort(this byte[] mode, int offset, short value)
   {
      BinaryPrimitives.WriteInt16LittleEndian(mode.AsSpan(offset), value);
      return mode;
   }



   /// <summary>Reads the fields flags.</summary>
   internal static uint Fields(byte[] mode) => BinaryPrimitives.ReadUInt32LittleEndian(mode.AsSpan(72));



   /// <summary>Reads one 16-bit setting at its offset.</summary>
   internal static short Short(byte[] mode, int offset) => BinaryPrimitives.ReadInt16LittleEndian(mode.AsSpan(offset));
}
