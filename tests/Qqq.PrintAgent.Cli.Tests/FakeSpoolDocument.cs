/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// One spool document <see cref="FakeWindowsPrinting"/> was asked to start: the queue it went to, its name, data type and
/// job number, the bytes the fake took, and whether it was ended or aborted. It is how a test sees what would have reached
/// a printer.
/// </summary>
internal sealed class FakeSpoolDocument(string? queue, string name, string dataType, uint job)
{
   /// <summary>The queue the document was started on.</summary>
   internal string? Queue { get; } = queue;



   /// <summary>The document name the spooler would show.</summary>
   internal string Name { get; } = name;



   /// <summary>The spool data type, RAW for ZPL.</summary>
   internal string DataType { get; } = dataType;



   /// <summary>The spooler job number the fake gave the document.</summary>
   internal uint Job { get; } = job;



   /// <summary>The bytes the fake spooler took, in order.</summary>
   internal MemoryStream Bytes { get; } = new();



   /// <summary>True once the document was finished, which is when the spooler may send it to the printer.</summary>
   internal bool Ended { get; set; }



   /// <summary>True once the document was discarded.</summary>
   internal bool Aborted { get; set; }
}
