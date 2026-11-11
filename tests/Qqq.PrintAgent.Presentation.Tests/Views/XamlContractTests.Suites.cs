/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>What the two interactive suites look up, and the identifiers that are generated instead of written out.</summary>
public sealed partial class XamlContractTests
{
   /// <summary>Identifiers the UI tests look up that the companion does not set, because AppKit's own views do.</summary>
   private static readonly Dictionary<string, string> s_setByAppKit = new(StringComparer.Ordinal)
   {
      ["AboutVersionText"] = "The version line of AppKit's About dialog.",
      ["AppKitSplash"] = "AppKit's splash overlay.",
   };

   /// <summary>
   /// The generated forms: the only identifiers that are not written out. Each is a stem, a dot and a position counted
   /// from one, and each names the one place in the companion that may set it, as "class | control | expression", the
   /// way a failure of <c>GeneratedIdentifiers_AreMadeOnlyWhereTheTableSays</c> reports that place. Nothing produces
   /// any of them yet (the tree and the rows by position come with the view step), so each place is null, which the
   /// same test holds: the companion writes no such identifier and no suite looks one up. The view step fills in the
   /// places; it does not change a rule.
   /// </summary>
   private static readonly GeneratedForm[] s_generatedForms =
   [
      new("Server", "A server's node in the tree, by its place in the tree counted from one; its name is the server's, and its pages are found through it (ADR-0005, \"Accessibility\").", ProducedAt: null),
      new("Printer", "A printer's row, by its place in the list; the controls inside it keep one identifier each and are found through the row (plan G17).", ProducedAt: null),
      new("Job", "A job's row, by its place on the page; the controls inside it are found through the row (plan G17).", ProducedAt: null),
   ];



   /// <summary>
   /// Every identifier the UI tests and the layout harness look up is one the companion sets, or one of AppKit's, and
   /// the identifiers they look up are the lines of <c>Views/interactive-test-ids.txt</c>. The first half fails when a
   /// view stops setting an identifier a suite still uses; the second makes what the suites depend on a reviewed list.
   /// Both suites are read in every folder of theirs, and a lookup in a form the reader does not know fails here
   /// rather than being skipped. An identifier of a generated form is held by the table of those forms instead.
   /// </summary>
   [Fact]
   public void IdentifiersTheInteractiveSuitesLookUp_AreSetByTheCompanion()
   {
      List<string> unreadable = [];
      SortedSet<string> lookedUp = InteractiveSuites.IdentifiersLookedUp(_suites, unreadable);
      var set = Places(unreadable).Select(place => place.Identifier).ToHashSet(StringComparer.Ordinal);
      string[] notSet = [.. lookedUp.Where(identifier => !set.Contains(identifier) && !s_setByAppKit.ContainsKey(identifier) && !s_generatedForms.Any(form => form.Covers(identifier)))];
      string[] notAppKits = [.. s_setByAppKit.Keys.Where(identifier => !lookedUp.Contains(identifier) || set.Contains(identifier))];

      Assert.True(unreadable.Count == 0, Report(unreadable));
      Assert.True(notSet.Length == 0, $"Looked up by the UI tests or the layout harness and set nowhere in the companion: {string.Join(", ", notSet)}. Those suites are not in the gate, so they would fail only when someone next runs them. Keep the identifier on the control, or move the test with the control in the same change.");
      Assert.True(notAppKits.Length == 0, $"Listed as set by AppKit, and either no longer looked up or now set by the companion: {string.Join(", ", notAppKits)}. Remove it from the list.");
      Assert.Equal(CompanionSources.Recorded(RecordedLookups), lookedUp, StringComparer.Ordinal);
   }



   /// <summary>The recorded lookups are one identifier a line, each a word of letters and digits that starts with a capital or an identifier of a generated form, in ordinal order and without a repeat, so that a change reads as one line.</summary>
   [Fact]
   public void RecordedLookups_AreWordsOrGeneratedFormsInOrder()
   {
      string[] lines = CompanionSources.Recorded(RecordedLookups);

      Assert.NotEmpty(lines);
      Assert.All(lines, line => Assert.True(Word().IsMatch(line) || s_generatedForms.Any(form => form.Covers(line)), $"\"{line}\" is neither a word of letters and digits that starts with a capital nor an identifier of a generated form ({string.Join(", ", s_generatedForms.Select(form => form.Form))})."));
      Assert.Equal(lines.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), lines, StringComparer.Ordinal);
   }



   /// <summary>
   /// Every element the layout harness finds by <c>x:Name</c> (with <c>FindName</c>, or as a field of the window) is
   /// declared in a view. The harness compiles the real views and is not built with the solution, so a renamed element
   /// would otherwise be found out only when someone next ran it. A name the reader cannot read fails here.
   /// </summary>
   [Fact]
   public void ElementNamesTheLayoutHarnessLooksUp_AreInTheViews()
   {
      List<string> unreadable = [];
      var declared = _markup.SelectMany(ViewMarkup.Elements).Select(element => element.Name).OfType<string>().ToHashSet(StringComparer.Ordinal);
      SortedSet<string> lookedUp = InteractiveSuites.ElementNamesLookedUp(_suites, unreadable);
      string[] missing = [.. lookedUp.Where(name => !declared.Contains(name))];

      Assert.NotEmpty(lookedUp);
      Assert.True(unreadable.Count == 0, Report(unreadable));
      Assert.True(missing.Length == 0, $"The layout harness looks these elements up by x:Name and no view declares them: {string.Join(", ", missing)}. Change the harness in the same change as the view.");
   }



   /// <summary>
   /// An identifier that is not written out (a binding in markup, an expression in code) is set only at the place the
   /// table of generated forms names for one of its forms, so the forms ADR-0005 allows are the only identifiers made
   /// at run time. A form marked as not produced yet is not produced: the companion writes no identifier that starts
   /// with its stem, and no suite looks one up.
   /// </summary>
   [Fact]
   public void GeneratedIdentifiers_AreMadeOnlyWhereTheTableSays()
   {
      List<(string Where, string Place)> notWrittenOut = [.. _markup.SelectMany(file => ViewMarkup.Elements(file)
         .Where(element => element.AutomationId is not null && element.AutomationId.StartsWith('{'))
         .Select(element => (element.Where, ViewMarkup.OwnerOf(file) + Separator + element.Control + Separator + element.AutomationId)))];
      notWrittenOut.AddRange(_reading.NotWrittenOut);

      List<string> problems = GeneratedForm.Problems(s_generatedForms, notWrittenOut, _markup.Concat(_code), InteractiveSuites.IdentifiersLookedUp(_suites, []));

      Assert.True(problems.Count == 0, Report(problems));
   }
}
