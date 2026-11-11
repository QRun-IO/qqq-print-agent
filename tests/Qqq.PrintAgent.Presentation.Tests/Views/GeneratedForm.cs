/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>
/// One form of identifier that is made while the companion runs instead of being written out: a stem, a dot and a
/// position counted from one, as in <c>Server.2</c>. ADR-0005 ("Accessibility") gives a server's tree node this form,
/// and the plan's G17 gives it to the rows of printers and jobs, because what they stand for has no name that is
/// fixed in the source. <c>ProducedAt</c> is the one place in the companion that may set such an identifier, written
/// as the contract reports it ("class | control | expression"); null while nothing produces the form yet.
/// </summary>
internal sealed record GeneratedForm(string Stem, string Meaning, string? ProducedAt)
{
   /// <summary>The form as it is written about: "Server.&lt;n&gt;".</summary>
   internal string Form => Stem + ".<n>";



   /// <summary>True for an identifier of this form: the stem, a dot, and a whole number from one, without a leading zero.</summary>
   internal bool Covers(string identifier)
      => identifier.Length > Stem.Length + 1 && identifier.StartsWith(Stem + ".", StringComparison.Ordinal) && identifier[Stem.Length + 1] != '0' && identifier.Skip(Stem.Length + 1).All(char.IsAsciiDigit);



   /// <summary>
   /// Holds the companion and the suites to a table of forms. An identifier that is not written out may be set only at
   /// the place a form names; a form that names a place is produced there and exactly once; and a form that is not
   /// produced yet is neither written by the companion (in a literal that starts with its stem and a dot) nor looked
   /// up by a suite, since nothing would answer.
   /// </summary>
   internal static List<string> Problems(IReadOnlyList<GeneratedForm> forms, IReadOnlyList<(string Where, string Place)> notWrittenOut, IEnumerable<SourceFile> companion, IEnumerable<string> lookedUp)
   {
      string all = string.Join(", ", forms.Select(form => form.Form));
      List<string> problems = [.. notWrittenOut.Where(made => !forms.Any(form => string.Equals(form.ProducedAt, made.Place, StringComparison.Ordinal)))
         .Select(made => $"{made.Where}: this identifier is not written out ({made.Place}), so it is made while the companion runs. Only the generated forms may be ({all}), each at the one place the table of generated forms in XamlContractTests names. If this is the place that makes one of them, set that form's ProducedAt to \"{made.Place}\". Otherwise write the identifier as a string literal, or pass it as a parameter from callers that each write a literal.")];
      SourceFile[] files = [.. companion];
      string[] lookups = [.. lookedUp];
      foreach(GeneratedForm form in forms)
      {
         if(form.ProducedAt is not null)
         {
            int found = notWrittenOut.Count(made => string.Equals(made.Place, form.ProducedAt, StringComparison.Ordinal));
            if(found != 1)
            {
               problems.Add($"{form.Form} is listed as produced at \"{form.ProducedAt}\", and {found} places set an identifier that is not written out there. Name the one place that produces it, or mark the form as not produced yet.");
            }
            continue;
         }
         problems.AddRange(files.SelectMany(file => Written(file, form.Stem)).Select(where => $"{where}: this writes an identifier of the form {form.Form}, which the table of generated forms marks as not produced yet. An identifier of that form is made from a position by the one place the table names, and written out nowhere. When the companion starts to produce it, name that place in the table in the same change."));
         problems.AddRange(lookups.Where(form.Covers).Select(identifier => $"{identifier} is looked up by the UI tests or the layout harness. It is of the form {form.Form}, which the table of generated forms in XamlContractTests marks as not produced yet, so nothing in the companion sets it. The change that makes a view produce the form names the producing place in the table; the lookup comes with that change or after it."));
      }
      return problems;
   }



   /// <summary>The places where a file starts a string, or an attribute's value, with the stem and a dot.</summary>
   private static IEnumerable<string> Written(SourceFile file, string stem)
   {
      string start = "\"" + stem + ".";
      for(int index = file.Code.IndexOf(start, StringComparison.Ordinal); index >= 0; index = file.Code.IndexOf(start, index + 1, StringComparison.Ordinal))
      {
         yield return file.At(index);
      }
   }
}
