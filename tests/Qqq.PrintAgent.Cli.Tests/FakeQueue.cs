/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// One print queue of <see cref="FakeWindowsPrinting"/>: what the spooler lists for it, the machine default settings it
/// hands out, what its driver answers about paper and document settings, and what a device context made for it reports.
/// A test describes the queue here and changes single answers to make the fake behave like a driver that misbehaves.
/// </summary>
internal sealed class FakeQueue(string? name, string? driver = "Synthetic label driver", string? port = "USB001")
{
   /// <summary>The queue name the spooler lists; null stands for a name Windows left out.</summary>
   internal string? Name { get; } = name;



   /// <summary>The driver name the spooler lists.</summary>
   internal string? Driver { get; set; } = driver;



   /// <summary>The port name the spooler lists.</summary>
   internal string? Port { get; set; } = port;



   /// <summary>The spooler status flags; zero is an idle, ready queue.</summary>
   internal uint Status { get; set; }



   /// <summary>When set, opening this queue fails with this Windows error.</summary>
   internal int? OpenError { get; set; }



   /// <summary>
   /// When set, this queue's driver never answers: every call that names the queue (opening it, asking its driver for its
   /// paper lists, creating a device context for it) is recorded and then throws what this makes. The exception stands for
   /// the hang, because a test cannot wait for a call that never returns. It is not a Windows error, so no rule can take
   /// it for a queue that merely has no settings; a rule that asks this queue anything fails.
   /// </summary>
   internal Func<Exception>? NeverAnswers { get; set; }



   /// <summary>The machine default settings (a complete DEVMODE with its private part), or null for a queue that has none.</summary>
   internal byte[]? MachineDefaults { get; set; }



   /// <summary>When set, the size the sizing call reports for the level 8 details instead of the real one.</summary>
   internal uint? ReportedDefaultsSize { get; set; }



   /// <summary>When set, reading the level 8 details fails with this Windows error.</summary>
   internal int? GetPrinterError { get; set; }



   /// <summary>Where the settings start inside the level 8 answer; the size of a pointer unless a test moves it.</summary>
   internal long DevModeOffset { get; set; } = IntPtr.Size;



   /// <summary>When true, the level 8 answer holds a null pointer where the settings should be.</summary>
   internal bool NullDevModePointer { get; set; }



   /// <summary>The paper identifiers the driver lists (capability 2).</summary>
   internal short[] PaperIds { get; set; } = [];



   /// <summary>The paper sizes the driver lists (capability 3), in tenths of a millimeter, in the same order as the identifiers.</summary>
   internal (int Width, int Height)[] PaperSizes { get; set; } = [];



   /// <summary>Answers to the counting call that replace the real number of entries, by capability.</summary>
   internal Dictionary<ushort, int> CountAnswers { get; } = [];



   /// <summary>Answers to the reading call that replace the real number of entries, by capability.</summary>
   internal Dictionary<ushort, int> ReadAnswers { get; } = [];



   /// <summary>When set, the size the driver reports for its document settings instead of the size of the machine defaults.</summary>
   internal int? SettingsSize { get; set; }



   /// <summary>What the driver returns from merging document settings; 1 means it accepted them.</summary>
   internal int MergeResult { get; set; } = 1;



   /// <summary>What the driver makes of the settings it is asked to merge; unchanged unless a test says otherwise.</summary>
   internal Func<byte[], byte[]> Merge { get; set; } = input => input.ToArray();



   /// <summary>When set, creating a device context for this queue fails with this Windows error.</summary>
   internal int? CreateContextError { get; set; }



   /// <summary>What a device context for this queue reports, by index; asking for an index that is not here fails the test.</summary>
   internal Dictionary<int, int> DeviceCaps { get; } = [];



   /// <summary>Describes a 300 dpi label device context: resolution, the physical page in pixels, no unprintable margin, and a printable area equal to the page.</summary>
   internal FakeQueue WithContext(int widthPixels, int heightPixels, int dpiX = 300, int dpiY = 300)
   {
      DeviceCaps[88] = dpiX;
      DeviceCaps[90] = dpiY;
      DeviceCaps[110] = widthPixels;
      DeviceCaps[111] = heightPixels;
      DeviceCaps[112] = 0;
      DeviceCaps[113] = 0;
      DeviceCaps[8] = widthPixels;
      DeviceCaps[10] = heightPixels;
      return this;
   }
}
