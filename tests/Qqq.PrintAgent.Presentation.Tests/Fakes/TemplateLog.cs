/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Tests.Fakes;

/// <summary>
/// A log of the kind that keeps an entry's values as well as its text, so a test can assert what was handed to the
/// log and not only what a text log would show of it: an object handed over as a value is kept by such a host even
/// when its text form is harmless.
/// </summary>
internal sealed class TemplateLog : IPresentationLog
{
   /// <summary>Every entry written so far as "LEVEL message", oldest first, with the template's holes filled.</summary>
   internal List<string> Entries { get; } = [];



   /// <summary>Every value handed over with a template, in the order they were given.</summary>
   internal List<object?> Values { get; } = [];



   /// <summary>Records an informational entry.</summary>
   public void Info(string message) => Entries.Add("INFO " + message);



   /// <summary>Records a warning entry.</summary>
   public void Warning(string message) => Entries.Add("WARN " + message);



   /// <summary>Records an error entry.</summary>
   public void Error(string message) => Entries.Add("ERROR " + message);



   /// <summary>Records a debug entry.</summary>
   public void Debug(string message) => Entries.Add("DEBUG " + message);



   /// <summary>Records an informational entry and its values.</summary>
   public void Info(string template, params object?[] values) => Keep("INFO ", template, values);



   /// <summary>Records a warning entry and its values.</summary>
   public void Warning(string template, params object?[] values) => Keep("WARN ", template, values);



   /// <summary>Records an error entry and its values.</summary>
   public void Error(string template, params object?[] values) => Keep("ERROR ", template, values);



   /// <summary>Records a debug entry and its values.</summary>
   public void Debug(string template, params object?[] values) => Keep("DEBUG ", template, values);



   /// <summary>Keeps the values and the entry's text.</summary>
   private void Keep(string level, string template, object?[] values)
   {
      Values.AddRange(values);
      Entries.Add(level + PresentationLogTemplate.Render(template, values));
   }
}
