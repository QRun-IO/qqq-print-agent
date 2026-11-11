/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text;

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// One shortfall found while checking a server: what kind it is and a plain sentence fragment saying exactly what was
/// wrong. A wizard shows the detail to an operator before anyone has signed in, so it is bounded twice: every piece of
/// text taken from the server goes through <see cref="Printable"/> first, and the whole detail is cut at
/// <see cref="DetailLimit"/> however many pieces were joined into it. A hostile value can therefore neither inject
/// lines into a log, reorder what a screen shows, nor flood either, and no detail ever contains a credential.
/// </summary>
public sealed record QqqCompatibilityProblem(QqqCompatibilityIssue Issue, string Detail)
{
   /// <summary>The most UTF-16 units of one server-supplied value that are shown; a version, a path or an address worth reading is shorter.</summary>
   internal const int ValueLimit = 80;

   /// <summary>The most UTF-16 units a detail holds before it is cut.</summary>
   internal const int DetailLimit = 512;

   /// <summary>
   /// The most UTF-16 units shown of longer text the server can influence: an address, a certificate name, an error
   /// description, a failure's message, and the identity values and listed addresses an operator has to compare. The
   /// short limit would cut two identifiers that share their first 80 characters to the same text.
   /// </summary>
   internal const int TextLimit = 512;

   /// <summary>The most values of a server-supplied list that are shown; the rest are counted.</summary>
   internal const int ListLimit = 32;

   private const string Cut = "...";



   /// <summary>What was wrong, in plain words: at most <see cref="DetailLimit"/> UTF-16 units, followed by an ellipsis when it was cut.</summary>
   public string Detail { get; } = Bounded(Detail);



   /// <summary>The check this problem belongs to, so a wizard can mark the earlier checks as passed.</summary>
   public QqqCompatibilityStage Stage => Issue switch
   {
      QqqCompatibilityIssue.ServerUnreachable => QqqCompatibilityStage.Connection,
      QqqCompatibilityIssue.SecureConnectionFailed => QqqCompatibilityStage.Security,
      QqqCompatibilityIssue.PrintingExtensionMissing or QqqCompatibilityIssue.PrintingExtensionTooOld or QqqCompatibilityIssue.PrintingExtensionUnsupported => QqqCompatibilityStage.Printing,
      QqqCompatibilityIssue.SignInUnavailable => QqqCompatibilityStage.SignIn,
      _ => QqqCompatibilityStage.Server,
   };



   /// <summary>
   /// Makes server-supplied text safe to show as part of a sentence: a problem's detail, or a line of the session
   /// client's log, which an operator reads too. Every code point that <see cref="ServerText.IsShown"/> does not show
   /// becomes a space: control characters, the line and paragraph separators, the format characters that reorder or
   /// hide text, and private-use and unassigned code points. Other space separators (U+00A0, U+3000) are kept as they
   /// are, and a surrogate without its partner is shown as U+FFFD. The text is read as whole code points and cut
   /// after the limit (<see cref="ValueLimit"/> UTF-16 units unless the caller names another), never in the middle
   /// of a surrogate pair, and reading stops there, so a value of any length costs the same. Because the result is a
   /// piece of a sentence, a cut is marked with an ellipsis after the limit and a missing value is shown as the words
   /// "(missing)". Neither suits a typed member of the management pipe, which has its own rule for that reason and
   /// shares only <see cref="ServerText.IsShown"/> with this one.
   /// </summary>
   internal static string Printable(string? value, int limit = ValueLimit)
   {
      if(string.IsNullOrEmpty(value))
      { return "(missing)"; }
      var shown = new StringBuilder();
      foreach(Rune rune in value.EnumerateRunes())
      {
         if(shown.Length + rune.Utf16SequenceLength > limit)
         { return shown.Append(Cut).ToString(); }
         shown.Append(ServerText.IsShown(rune) ? rune.ToString() : " ");
      }
      return shown.ToString();
   }



   /// <summary>Joins a server-supplied list for display: each value printable and cut at the given limit, at most <see cref="ListLimit"/> of them, and a count of the rest, so neither one value nor the number of values can make a line unbounded.</summary>
   internal static string PrintableList(IEnumerable<string?>? values, int limit = ValueLimit) => BoundedList((values ?? []).Select(value => Printable(value, limit)));



   /// <summary>Joins items that are already printable, showing at most <see cref="ListLimit"/> of them and counting the rest.</summary>
   internal static string BoundedList(IEnumerable<string> items)
   {
      string[] all = [.. items];
      string shown = string.Join(", ", all.Take(ListLimit));
      return all.Length > ListLimit ? shown + $", and {all.Length - ListLimit} more" : shown;
   }



   /// <summary>Cuts a detail at the limit, one unit earlier when the limit falls inside a surrogate pair, and marks the cut.</summary>
   private static string Bounded(string detail)
   {
      if(detail.Length <= DetailLimit)
      { return detail; }
      int end = char.IsHighSurrogate(detail[DetailLimit - 1]) ? DetailLimit - 1 : DetailLimit;
      return string.Concat(detail.AsSpan(0, end), Cut);
   }
}
