/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// One small text file of diagnostic lines. An implementation only stores and returns lines; what is written, when the
/// file is started afresh and which lines count as recent is decided by <see cref="BrowserCallbackTrace"/>.
/// </summary>
internal interface ITraceFile
{
   /// <summary>The size of the file in bytes; zero when it does not exist.</summary>
   long Length { get; }



   /// <summary>Removes the file; nothing happens when it does not exist.</summary>
   void Delete();



   /// <summary>Adds one line at the end, creating the file and its folder when they are missing.</summary>
   void Append(string line);



   /// <summary>The lines of the file, oldest first; none when it does not exist.</summary>
   IEnumerable<string> ReadLines();
}