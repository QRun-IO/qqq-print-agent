/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Text;

namespace Qqq.PrintAgent.Presentation;

/// <summary>
/// Turns a message template and its values into the text of a log entry, for a host whose log keeps text. A template
/// is fixed text with named holes, "Opened {Server} after {Seconds} s", and the values fill the holes in the order
/// they are given. Writing entries this way keeps what was said apart from what it was said about, so the sentence
/// is the same for every server and a host can keep the values as fields.
/// </summary>
public static class PresentationLogTemplate
{
   /// <summary>
   /// Fills the holes of a template, first hole with the first value. Each value is written in the invariant culture,
   /// so an entry reads the same on every computer, and a missing value (null) is written as nothing. A hole that has
   /// no value left stays as it was written, which shows in the log that a value was forgotten; a value that has no
   /// hole is left out. Only the template is searched for holes, so a brace inside a value is never taken for one.
   /// </summary>
   public static string Render(string template, IReadOnlyList<object?> values)
   {
      ArgumentNullException.ThrowIfNull(template);
      ArgumentNullException.ThrowIfNull(values);
      StringBuilder text = new(template.Length);
      int position = 0;
      foreach(object? value in values)
      {
         int open = template.IndexOf('{', position);
         int close = open < 0 ? -1 : template.IndexOf('}', open);
         if(close < 0)
         {
            break;
         }
         text.Append(template, position, open - position).Append(Convert.ToString(value, CultureInfo.InvariantCulture));
         position = close + 1;
      }
      return text.Append(template, position, template.Length - position).ToString();
   }
}
