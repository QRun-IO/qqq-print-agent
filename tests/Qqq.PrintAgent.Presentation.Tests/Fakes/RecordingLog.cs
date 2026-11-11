/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Tests.Fakes;

/// <summary>A log that keeps every entry in order with its level, so a test can assert exactly what presentation logic recorded and that nothing secret was written.</summary>
internal sealed class RecordingLog : IPresentationLog
{
   /// <summary>Every entry written so far as "LEVEL message", oldest first. It can be read while the code under test writes on another thread.</summary>
   internal RecordedList<string> Entries { get; } = new();



   /// <summary>Records an informational entry.</summary>
   public void Info(string message) => Entries.Add("INFO " + message);



   /// <summary>Records a warning entry.</summary>
   public void Warning(string message) => Entries.Add("WARN " + message);



   /// <summary>Records an error entry.</summary>
   public void Error(string message) => Entries.Add("ERROR " + message);



   /// <summary>Records a debug entry.</summary>
   public void Debug(string message) => Entries.Add("DEBUG " + message);



   /// <summary>Completes when an entry that is wanted has been written, at once when one already has. An entry that is written on another thread than the test's is waited for here (<see cref="RecordedList{T}.Added"/>).</summary>
   internal Task Written(Predicate<string> wanted) => Entries.Added(wanted);
}
