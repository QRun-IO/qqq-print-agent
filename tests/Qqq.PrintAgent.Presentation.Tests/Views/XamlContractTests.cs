/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.RegularExpressions;

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>
/// What the companion's views promise to automation, held without rendering them: every control an operator acts on
/// has an automation identifier, each identifier is on the control it is recorded on, and the texts that report
/// status declare a live region (ADR-0005, "Accessibility"). The UI tests and the layout harness find controls by
/// these identifiers and are not in the gate, so this is where a renamed, dropped or moved identifier is noticed.
/// <para>
/// The views are read as text: the project links the companion's markup and code-behind beside the tests and
/// references neither WinUI nor the companion. An identifier here is one the source writes out, with
/// <c>AutomationProperties.AutomationId</c> in markup or <c>SetAutomationId</c> in code. An <c>x:Name</c> is not one,
/// although WinUI reports it as the identifier when no other is set: it names a field, and a field is renamed freely.
/// </para>
/// <para>
/// A kind that is in none of the contract's lists fails until it is put in one, in markup and in code alike. (The
/// message of the commit that added these tests says so without the qualification; at that commit it held for markup
/// only.) What is covered and what is not: every element of every view's markup, and every object the companion's
/// code creates with <c>new</c>. Not covered are the choices of a combo box, which are found through their box; the
/// buttons of a content dialog, which are the platform's and carry its names; the views of AppKit (the About dialog,
/// the splash), which set their own identifiers; the tray icon's menu, which Windows draws as a native menu; and a
/// text that code creates, such as a row's cells. The class is split by concern: the kinds and the controls in
/// XamlContractTests.Kinds.cs, the texts in XamlContractTests.Texts.cs, the suites and the generated forms in
/// XamlContractTests.Suites.cs.
/// </para>
/// </summary>
public sealed partial class XamlContractTests
{
   private const string RecordedIdentifiers = "automation-ids.txt";
   private const string RecordedLookups = "interactive-test-ids.txt";
   private const string Separator = " | ";

   private readonly IReadOnlyList<SourceFile> _markup = CompanionSources.Markup();
   private readonly IReadOnlyList<SourceFile> _code = CompanionSources.Code();
   private readonly IReadOnlyList<SourceFile> _suites = CompanionSources.InteractiveSuites();
   private readonly CodeBehind.Reading _reading = CodeBehind.Read(CompanionSources.Code());



   /// <summary>One identifier the companion writes out, with the control it is on as the recorded list describes it and the line a failure points to.</summary>
   private sealed record Place(string Identifier, string Owner, string Control, string Where)
   {
      /// <summary>The line of the recorded list for this place.</summary>
      internal string Line => Identifier + Separator + Owner + Separator + Control;
   }



   /// <summary>
   /// The contract reads the real sources: the window's markup and code-behind and both interactive suites are among
   /// the linked files, and nothing a build generated is. Without this a link that stopped matching would leave the
   /// other tests comparing against less than the companion.
   /// </summary>
   [Fact]
   public void TheContract_ReadsTheCompanionAndBothInteractiveSuites()
   {
      Assert.Contains("MainWindow.xaml", _markup.Select(file => file.Name), StringComparer.Ordinal);
      Assert.Contains("MainWindow.xaml.cs", _code.Select(file => file.Name), StringComparer.Ordinal);
      Assert.Contains("UiTests/AppTests.cs", _suites.Select(file => file.Name), StringComparer.Ordinal);
      Assert.Contains("LayoutTests/LayoutScenario.cs", _suites.Select(file => file.Name), StringComparer.Ordinal);
      Assert.DoesNotContain(_markup.Concat(_code).Concat(_suites), file => file.Name.Split('/').Any(part => part is "obj" or "bin") || file.Name.EndsWith(".g.cs", StringComparison.Ordinal));
   }



   /// <summary>
   /// The identifiers the companion sets, each with the control it is on, are exactly the lines of
   /// <c>Views/automation-ids.txt</c>: "Identifier | class | control". The class is the view's, or the one the C# file
   /// declares, so a file that is only renamed changes nothing. A renamed or removed identifier is therefore a changed
   /// line in a reviewed file, a new one is a new line, and an identifier that moves to another control changes its
   /// line too, so two identifiers that swap controls are two changed lines. An identifier that code sets in a way
   /// that cannot be read fails here, because the set would then be a guess.
   /// </summary>
   [Fact]
   public void Identifiers_AreTheRecordedSet()
   {
      List<string> problems = [];
      List<Place> places = Places(problems);
      (string Identifier, string Line)[] recorded = [.. CompanionSources.Recorded(RecordedIdentifiers).Select(line => (line.Split(Separator)[0], line))];
      ILookup<string, string> set = places.ToLookup(place => place.Identifier, place => place.Line, StringComparer.Ordinal);
      var known = recorded.Select(line => line.Identifier).ToHashSet(StringComparer.Ordinal);
      string[] gone = [.. recorded.Where(line => !set.Contains(line.Identifier)).Select(line => line.Identifier)];
      string[] unrecorded = [.. places.Where(place => !known.Contains(place.Identifier)).Select(place => place.Line).Order(StringComparer.Ordinal)];
      string[] moved = [.. recorded.Where(line => set.Contains(line.Identifier) && !set[line.Identifier].Contains(line.Line, StringComparer.Ordinal))
         .Select(line => $"   {line.Line}{Environment.NewLine}      is now: {string.Join(" and ", set[line.Identifier])}")];
      if(gone.Length > 0)
      {
         problems.Add($"Recorded in Views/{RecordedIdentifiers} and no longer set anywhere in the companion: {string.Join(", ", gone)}. An identifier is not renamed or removed while its control exists: the UI tests and the layout harness find controls by it. Where the control is replaced, the test that uses it moves in the same change, and only then does the line go.");
      }
      if(moved.Length > 0)
      {
         problems.Add($"Recorded on one control and now on another:{Environment.NewLine}{string.Join(Environment.NewLine, moved)}{Environment.NewLine}An identifier stays on its control: the UI tests and the layout harness press what they find by it. If the identifier went to a different control, put it back. If it is the same control under a new name or label, or in another class, change its line in Views/{RecordedIdentifiers} to the new description.");
      }
      if(unrecorded.Length > 0)
      {
         problems.Add($"Set by the companion and not recorded. Add each line to Views/{RecordedIdentifiers}, in order:{Environment.NewLine}{string.Join(Environment.NewLine, unrecorded.Select(line => "   " + line))}");
      }

      Assert.True(problems.Count == 0, Report(problems));
   }



   /// <summary>
   /// Each identifier is written at one place in the companion, so it is unique within its file and across the views. A
   /// row is built from one place many times, and its controls are found through the row, which is found by position
   /// (ADR-0005, "Accessibility"); two different controls never share an identifier. One control carries one
   /// identifier, and two controls the recorded list could not tell apart are named until it can. An element that has
   /// no identifier of its own must not be named like one: WinUI reports an element's <c>x:Name</c> as its identifier
   /// when it has none, so an automation client that looks the identifier up would find that element first.
   /// </summary>
   [Fact]
   public void Identifiers_AreEachSetInOnePlace()
   {
      List<string> problems = [];
      List<Place> places = Places(problems);
      problems.AddRange(places.GroupBy(place => place.Identifier, StringComparer.Ordinal).Where(same => same.Count() > 1)
         .Select(same => $"{same.Key} is set at {string.Join(" and at ", same.Select(place => place.Where))}. Two controls never share an identifier: give the second its own."));
      problems.AddRange(places.GroupBy(place => place.Owner + Separator + place.Control, StringComparer.Ordinal).Where(same => same.Select(place => place.Identifier).Distinct(StringComparer.Ordinal).Count() > 1)
         .Select(same => $"{string.Join(" and ", same.Select(place => place.Identifier).Distinct(StringComparer.Ordinal))} are all recorded as \"{same.Key}\" ({string.Join(", ", same.Select(place => place.Where))}). If that is one control, it carries one identifier. If they are different controls, the recorded list cannot tell them apart, and a swap of their identifiers would go unnoticed: give one an x:Name in markup, or a variable of its own in code."));
      ILookup<string, Place> byIdentifier = places.ToLookup(place => place.Identifier, StringComparer.Ordinal);
      foreach(SourceFile file in _markup)
      {
         var namedInCode = CompanionSources.CodeBehindOf(file, _code).SelectMany(CodeBehind.Named).Select(named => named.Target).ToHashSet(StringComparer.Ordinal);
         problems.AddRange(ViewMarkup.Elements(file)
            .Where(element => element.Name is not null && string.IsNullOrWhiteSpace(element.AutomationId) && !namedInCode.Contains(element.Name) && byIdentifier.Contains(element.Name))
            .Select(element => $"{element.Where} has the x:Name {element.Name} and no identifier of its own, and {element.Name} is the identifier set at {byIdentifier[element.Name!].First().Where}. WinUI reports an element's name as its identifier when it has none, so a lookup of {element.Name} could find this element instead. Rename the element, or give it an identifier of its own."));
      }

      Assert.True(problems.Count == 0, Report(problems));
   }



   /// <summary>
   /// The recorded identifiers are one a line, "Identifier | class | control", the identifier a word of letters and
   /// digits that starts with a capital (nothing generated, no position, no name of a server), in ordinal order and
   /// without a repeat, so that a change reads as one line.
   /// </summary>
   [Fact]
   public void RecordedIdentifiers_AreAWordAClassAndAControlInOrder()
   {
      string[] lines = CompanionSources.Recorded(RecordedIdentifiers);
      string[][] parts = [.. lines.Select(line => line.Split(Separator, 3))];

      Assert.NotEmpty(lines);
      Assert.All(parts, part => Assert.True(part.Length == 3 && Word().IsMatch(part[0]) && part.All(text => text.Length > 0 && string.Equals(text, text.Trim(), StringComparison.Ordinal)), $"\"{string.Join(Separator, part)}\" is not \"Identifier | class | control\" with a word as the identifier."));
      Assert.Equal(lines.Order(StringComparer.Ordinal), lines, StringComparer.Ordinal);
      Assert.Equal(parts.Select(part => part[0]).Distinct(StringComparer.Ordinal), parts.Select(part => part[0]), StringComparer.Ordinal);
   }



   /// <summary>Every place an identifier is written out, in markup and in code, and what the code readers could not read. An identifier that is not written out (a binding, an expression) is not among them: the table of generated forms holds those.</summary>
   private List<Place> Places(List<string> unreadable)
   {
      List<Place> places = [.. _markup.SelectMany(file => ViewMarkup.Elements(file)
         .Where(element => !string.IsNullOrWhiteSpace(element.AutomationId) && !element.AutomationId.StartsWith('{'))
         .Select(element => new Place(element.AutomationId!, ViewMarkup.OwnerOf(file), element.Control, element.Where)))];
      places.AddRange(_reading.Identifiers.Select(identifier => new Place(identifier.Value, identifier.Owner, identifier.Control, identifier.Where)));
      unreadable.AddRange(_reading.Unreadable);
      return places;
   }



   /// <summary>Joins what a test found into one message, each finding once and on its own line.</summary>
   private static string Report(IEnumerable<string> problems) => Environment.NewLine + string.Join(Environment.NewLine, problems.Distinct(StringComparer.Ordinal));



   /// <summary>A word of letters and digits that starts with a capital letter.</summary>
   [GeneratedRegex("^[A-Z][A-Za-z0-9]*$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex Word();
}
