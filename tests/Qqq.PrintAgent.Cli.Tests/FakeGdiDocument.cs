/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// One GDI document <see cref="FakeWindowsPrinting"/> was asked to start on a device context: the queue it went to, its
/// name and job number, how many pages were started and ended, the bitmaps drawn, and whether it was ended or aborted.
/// </summary>
internal sealed class FakeGdiDocument(string? queue, string name, int job)
{
   /// <summary>The queue the document was started on.</summary>
   internal string? Queue { get; } = queue;



   /// <summary>The document name the spooler would show.</summary>
   internal string Name { get; } = name;



   /// <summary>The spooler job number the fake gave the document.</summary>
   internal int Job { get; } = job;



   /// <summary>How many pages were started.</summary>
   internal int PagesStarted { get; set; }



   /// <summary>How many pages were ended, which is when the driver would emit them.</summary>
   internal int PagesEnded { get; set; }



   /// <summary>The pixel data of every bitmap drawn, in order.</summary>
   internal List<byte[]> Drawn { get; } = [];



   /// <summary>True once the document was ended, which hands it to the spooler.</summary>
   internal bool Ended { get; set; }



   /// <summary>True once the document was cancelled.</summary>
   internal bool Aborted { get; set; }
}
