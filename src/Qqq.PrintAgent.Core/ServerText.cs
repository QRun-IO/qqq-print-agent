/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Text;

namespace Qqq.PrintAgent.Core;

/// <summary>
/// The one definition of which characters are shown as they are when text that a server supplied, or that an operator
/// typed, is put on a screen, in a log or on the management pipe. It is a type of its own because several things
/// depend on it and none of them owns it: the session client's problem details and log lines
/// (<see cref="Authentication.QqqCompatibilityProblem"/>), the form of text on the management pipe, and the rule for
/// what a server's name may be. Each of those decides for itself how long the text may be and what stands in for a
/// missing value; none of them decides again which characters are safe.
/// </summary>
public static class ServerText
{
   /// <summary>
   /// True for a code point that shows as itself and cannot change how the text around it is laid out. False for
   /// control characters, the line and paragraph separators (U+2028, U+2029) that end a line as surely as a newline,
   /// the format characters that reorder or hide text (the bidirectional embeddings, overrides and isolates, the
   /// zero-width characters, the invisible tag characters), and private-use and unassigned code points. Space
   /// separators other than the plain space (the no-break space U+00A0, the ideographic space U+3000) are shown: they
   /// are visible gaps and move nothing. A surrogate without its partner is not a code point and never arrives here
   /// as one: reading text by code point replaces it with U+FFFD, which is shown.
   /// </summary>
   public static bool IsShown(Rune rune) => Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format
      or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned);
}
