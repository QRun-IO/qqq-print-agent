/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.RegularExpressions;

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>
/// What the companion's views promise to automation, held without rendering them: every control an operator acts on
/// has an automation identifier, the identifiers are the recorded set, and the texts that report status declare a live
/// region (ADR-0005, "Accessibility"). The UI tests and the layout harness find controls by these identifiers and are
/// not in the gate, so this is where a renamed or dropped identifier is noticed.
/// <para>
/// The views are read as text: the project links the companion's markup and code-behind beside the tests and
/// references neither WinUI nor the companion. An identifier here is one the source writes out, with
/// <c>AutomationProperties.AutomationId</c> in markup or <c>SetAutomationId</c> in code. An <c>x:Name</c> is not one,
/// although WinUI reports it as the identifier when no other is set: it names a field, and a field is renamed freely.
/// </para>
/// </summary>
public sealed partial class XamlContractTests
{
   private const string RecordedIdentifiers = "automation-ids.txt";
   private const string RecordedLookups = "interactive-test-ids.txt";
   private const string FooterSaysIt = "It says in more detail what the footer's status line says. The footer is the one live region for the state of the connection, so a change is announced once.";
   private const string MessageBarSaysIt = "It shows the state of Windows as last read. The result of the operator's action is announced by the message bar, and this text is read on demand.";
   private const string NeverQuiet = "It changes with every contact with the server, or every second. A live region here would never be quiet.";
   private const string DescribesAList = "It describes the list on its page and follows every refresh and every filter keystroke. The rows are what the operator reads.";
   private const string NamesSomething = "It names or describes something and reports no result: a heading, a label, or a detail that is read on demand.";

   /// <summary>
   /// The interactive kinds: what an operator acts on with the keyboard or the pointer. They are WinUI's controls that
   /// are pressed or followed, switched, typed in, chosen from, selected in, or expanded, and the items of menus,
   /// navigation, tabs and trees. Each must carry an identifier wherever it appears, in markup or created in code. A
   /// class of the companion that derives from one of these is interactive too.
   /// </summary>
   private static readonly HashSet<string> s_interactive = new(StringComparer.Ordinal)
   {
      "Button", "HyperlinkButton", "Hyperlink", "RepeatButton", "DropDownButton", "SplitButton", "AppBarButton",
      "ToggleButton", "ToggleSplitButton", "AppBarToggleButton", "CheckBox", "RadioButton", "RadioButtons", "ToggleSwitch",
      "TextBox", "PasswordBox", "RichEditBox", "AutoSuggestBox", "NumberBox",
      "ComboBox", "Slider", "RatingControl", "ColorPicker", "CalendarDatePicker", "CalendarView", "DatePicker", "TimePicker",
      "ListView", "GridView", "ListBox", "FlipView", "TreeView", "ItemsView", "NavigationView", "TabView", "Pivot", "SelectorBar", "BreadcrumbBar", "PipsPager",
      "Expander",
      "MenuBarItem", "MenuFlyoutItem", "ToggleMenuFlyoutItem", "RadioMenuFlyoutItem", "MenuFlyoutSubItem", "NavigationViewItem", "TabViewItem", "SelectorBarItem", "TreeViewItem",
   };

   /// <summary>The text kinds: what shows words the window can change while it is shown. These are the candidates for a live region.</summary>
   private static readonly HashSet<string> s_texts = new(StringComparer.Ordinal) { "TextBlock", "RichTextBlock", "InfoBar" };

   /// <summary>
   /// The passive kinds: everything else a view contains today. Layout (Grid, StackPanel, Border, ScrollViewer and the
   /// row and column definitions); pictures (Image, Ellipse, SymbolIcon); resources, styles and the window's backdrop;
   /// a keyboard accelerator, which is a key and not an element; the containers whose parts carry the identifiers
   /// (Window, MenuBar, and ContentDialog, whose three buttons are the platform's own and are named PrimaryButton,
   /// SecondaryButton and CloseButton by its template); a menu's separator; and ComboBoxItem, a choice that is found
   /// through its box by name or position, as a row is found through its list (ADR-0005, "Accessibility").
   /// </summary>
   private static readonly HashSet<string> s_passive = new(StringComparer.Ordinal)
   {
      "Grid", "StackPanel", "Border", "ScrollViewer", "RowDefinition", "ColumnDefinition",
      "Image", "Ellipse", "SymbolIcon",
      "Application", "ResourceDictionary", "XamlControlsResources", "Style", "Setter", "StaticResource", "MicaBackdrop",
      "KeyboardAccelerator",
      "Window", "MenuBar", "ContentDialog",
      "MenuFlyoutSeparator", "ComboBoxItem",
   };

   /// <summary>
   /// Controls created in code that have no identifier, by file and kind, with how many and why. The count is exact: one
   /// more in that file fails, and so does one fewer, so an exception cannot outlive what it excuses.
   /// </summary>
   private static readonly (string File, string Kind, int Count, string Reason)[] s_createdWithoutAnIdentifier =
   [
      ("MainWindow.xaml.cs", "MenuFlyoutItem", 4, "They are the tray icon's menu. H.NotifyIcon draws that menu as a native Windows popup menu built from these items (its default ContextMenuMode, PopupMenu, and the companion sets no other), so they are never elements on screen and an identifier on them would reach no automation client."),
   ];

   /// <summary>Identifiers the UI tests look up that the companion does not set, because AppKit's own views do.</summary>
   private static readonly Dictionary<string, string> s_setByAppKit = new(StringComparer.Ordinal)
   {
      ["AboutVersionText"] = "The version line of AppKit's About dialog.",
      ["AppKitSplash"] = "AppKit's splash overlay.",
   };

   /// <summary>
   /// The status texts, by <c>x:Name</c>, each with the live region it declares. Polite is the rule. Assertive
   /// interrupts what is being read and is for an error the operator must act on at once; there is none.
   /// FeedbackBar is an InfoBar, which WinUI announces itself when it opens; it has declared Polite since 1.x and is held
   /// as it stands.
   /// </summary>
   private static readonly Dictionary<string, string> s_statusTexts = new(StringComparer.Ordinal)
   {
      ["FooterStatus"] = "Polite",
      ["FeedbackBar"] = "Polite",
      ["UpdateStatusText"] = "Polite",
      ["LastUpdateOutcomeText"] = "Polite",
   };

   /// <summary>The texts the window changes that are deliberately not live regions, by <c>x:Name</c>, each with the reason.</summary>
   private static readonly Dictionary<string, string> s_notAnnounced = new(StringComparer.Ordinal)
   {
      ["ConnectionFeedback"] = "It is an InfoBar. WinUI announces an InfoBar itself when it opens, with a UI Automation notification that carries its severity, title and message, and says nothing when an open one changes (InfoBar guidance, \"Updating the InfoBar\"). A live region on it would be a second announcement, and it shows the very message FeedbackBar shows at that moment. It goes with the connection dialog (ADR-0005).",
      ["StatusText"] = FooterSaysIt,
      ["ConnectionText"] = FooterSaysIt,
      ["AuthenticationText"] = FooterSaysIt,
      ["TransportStateText"] = FooterSaysIt,
      ["FooterContact"] = NeverQuiet,
      ["TransportTrafficText"] = NeverQuiet,
      ["ServiceStateText"] = MessageBarSaysIt,
      ["ServiceGuidanceText"] = MessageBarSaysIt,
      ["StartupGuidanceText"] = MessageBarSaysIt,
      ["PrintersSummary"] = DescribesAList,
      ["PrintersEmptyText"] = DescribesAList,
      ["JobsSummary"] = DescribesAList,
      ["JobsEmptyTitle"] = DescribesAList,
      ["JobsEmptyMessage"] = DescribesAList,
      ["JobsNotice"] = DescribesAList,
      ["ProductHeading"] = NamesSomething,
      ["CompanyHeading"] = NamesSomething,
      ["PageTitle"] = NamesSomething,
      ["PageDescription"] = NamesSomething,
      ["FooterServerLabel"] = NamesSomething,
      ["DeviceProfileText"] = NamesSomething,
      ["VersionText"] = NamesSomething,
      ["UpdateChannelText"] = NamesSomething,
      ["AllowUntrustedCertificateLabel"] = NamesSomething,
   };

   private readonly IReadOnlyList<SourceFile> _markup = CompanionSources.Markup();
   private readonly IReadOnlyList<SourceFile> _code = CompanionSources.Code();
   private readonly IReadOnlyList<SourceFile> _suites = CompanionSources.InteractiveSuites();



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
   /// Every element of an interactive kind in a view has an identifier, written in the markup or set on its
   /// <c>x:Name</c> in the view's code-behind; and every control of an interactive kind that code creates is named in a
   /// <c>SetAutomationId</c> of the same file, apart from the counted exceptions. A kind that appears in a view and is
   /// in none of the three lists fails here as well, so a new kind of control cannot arrive unclassified and therefore
   /// unidentified.
   /// </summary>
   [Fact]
   public void EveryInteractiveControl_HasAnIdentifier()
   {
      List<string> problems = [];
      HashSet<string> interactive = InteractiveKinds();
      foreach(SourceFile file in _markup)
      {
         var namedInCode = CompanionSources.CodeBehindOf(file, _code).SelectMany(behind => CodeBehind.Identifiers(behind, problems)).Select(identifier => identifier.Target).ToHashSet(StringComparer.Ordinal);
         foreach(ViewMarkup.Element element in ViewMarkup.Elements(file))
         {
            if(interactive.Contains(element.Kind))
            {
               if(string.IsNullOrWhiteSpace(element.AutomationId) && (element.Name is null || !namedInCode.Contains(element.Name)))
               {
                  problems.Add($"{element.Where} has no identifier. Give it AutomationProperties.AutomationId, named like the others in Views/{RecordedIdentifiers}, and record it there.");
               }
            }
            else if(!s_texts.Contains(element.Kind) && !s_passive.Contains(element.Kind) && !element.Kind.StartsWith("x:", StringComparison.Ordinal))
            {
               problems.Add($"{element.Where}: no view has had a <{element.Kind}> before, and the contract does not know the kind. Add it to the interactive kinds if an operator acts on it (it then needs an identifier), to the text kinds if it shows words the window changes, and to the passive kinds otherwise.");
            }
         }
      }
      problems.AddRange(CreatedWithoutAnIdentifier(interactive));

      Assert.True(problems.Count == 0, Report(problems));
   }



   /// <summary>
   /// The identifiers the companion sets are exactly the lines of <c>Views/automation-ids.txt</c>. A renamed or
   /// removed identifier is therefore a changed line in a reviewed file, and a new one is a new line. An identifier
   /// that code sets in a way that cannot be read fails here, because the set would then be a guess.
   /// </summary>
   [Fact]
   public void Identifiers_AreTheRecordedSet()
   {
      List<string> unreadable = [];
      SortedSet<string> set = new(Places(unreadable).Select(place => place.Identifier), StringComparer.Ordinal);
      string[] recorded = CompanionSources.Recorded(RecordedIdentifiers);
      string[] gone = [.. recorded.Except(set, StringComparer.Ordinal)];
      string[] unrecorded = [.. set.Except(recorded, StringComparer.Ordinal)];
      List<string> problems = [.. unreadable];
      if(gone.Length > 0)
      {
         problems.Add($"Recorded in Views/{RecordedIdentifiers} and no longer set anywhere in the companion: {string.Join(", ", gone)}. An identifier is not renamed or removed while its control exists: the UI tests and the layout harness find controls by it. Where the control is replaced, the test that uses it moves in the same change, and only then does the line go.");
      }
      if(unrecorded.Length > 0)
      {
         problems.Add($"Set by the companion and not recorded: {string.Join(", ", unrecorded)}. Add each to Views/{RecordedIdentifiers}, in order.");
      }

      Assert.True(problems.Count == 0, Report(problems));
   }



   /// <summary>
   /// Each identifier is written at one place in the companion, so it is unique within its file and across the views. A
   /// row is built from one place many times, and its controls are found through the row, which is found by position
   /// (ADR-0005, "Accessibility"); two different controls never share an identifier.
   /// </summary>
   [Fact]
   public void Identifiers_AreEachSetInOnePlace()
   {
      List<string> unreadable = [];
      string[] repeated = [.. Places(unreadable).GroupBy(place => place.Identifier, StringComparer.Ordinal).Where(places => places.Count() > 1)
         .Select(places => $"{places.Key} is set at {string.Join(" and at ", places.Select(place => place.Where))}.")];

      Assert.True(unreadable.Count == 0, Report(unreadable));
      Assert.True(repeated.Length == 0, Report(repeated));
   }



   /// <summary>A recorded list is one identifier a line, each a word of letters and digits that starts with a capital (nothing generated, no position, no name of a server), in ordinal order and without a repeat, so that a change to it reads as one line.</summary>
   [Theory]
   [InlineData(RecordedIdentifiers)]
   [InlineData(RecordedLookups)]
   public void RecordedList_IsWordsInOrderWithoutARepeat(string name)
   {
      string[] lines = CompanionSources.Recorded(name);

      Assert.NotEmpty(lines);
      Assert.All(lines, line => Assert.Matches(Word(), line));
      Assert.Equal(lines.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), lines, StringComparer.Ordinal);
   }



   /// <summary>
   /// Every identifier the UI tests and the layout harness look up is one the companion sets, or one of AppKit's, and
   /// the identifiers they look up are the lines of <c>Views/interactive-test-ids.txt</c>. The first half fails when a
   /// view stops setting an identifier a suite still uses; the second makes what the suites depend on a reviewed list.
   /// </summary>
   [Fact]
   public void IdentifiersTheInteractiveSuitesLookUp_AreSetByTheCompanion()
   {
      List<string> unreadable = [];
      SortedSet<string> lookedUp = InteractiveSuites.IdentifiersLookedUp(_suites, unreadable);
      var set = Places(unreadable).Select(place => place.Identifier).ToHashSet(StringComparer.Ordinal);
      string[] notSet = [.. lookedUp.Where(identifier => !set.Contains(identifier) && !s_setByAppKit.ContainsKey(identifier))];
      string[] notAppKits = [.. s_setByAppKit.Keys.Where(identifier => !lookedUp.Contains(identifier) || set.Contains(identifier))];

      Assert.True(unreadable.Count == 0, Report(unreadable));
      Assert.True(notSet.Length == 0, $"Looked up by the UI tests or the layout harness and set nowhere in the companion: {string.Join(", ", notSet)}. Those suites are not in the gate, so they would fail only when someone next runs them. Keep the identifier on the control, or move the test with the control in the same change.");
      Assert.True(notAppKits.Length == 0, $"Listed as set by AppKit, and either no longer looked up or now set by the companion: {string.Join(", ", notAppKits)}. Remove it from the list.");
      Assert.Equal(CompanionSources.Recorded(RecordedLookups), lookedUp, StringComparer.Ordinal);
   }



   /// <summary>
   /// Every element the layout harness finds by <c>x:Name</c> (with <c>FindName</c>, or as a field of the window) is
   /// declared in a view. The harness compiles the real views and is not built with the solution, so a renamed element
   /// would otherwise be found out only when someone next ran it.
   /// </summary>
   [Fact]
   public void ElementNamesTheLayoutHarnessLooksUp_AreInTheViews()
   {
      var declared = _markup.SelectMany(ViewMarkup.Elements).Select(element => element.Name).OfType<string>().ToHashSet(StringComparer.Ordinal);
      SortedSet<string> lookedUp = InteractiveSuites.ElementNamesLookedUp(_suites);
      string[] missing = [.. lookedUp.Where(name => !declared.Contains(name))];

      Assert.NotEmpty(lookedUp);
      Assert.True(missing.Length == 0, $"The layout harness looks these elements up by x:Name and no view declares them: {string.Join(", ", missing)}. Change the harness in the same change as the view.");
   }



   /// <summary>
   /// Every text the window changes while it is shown is either a status text, and then declares its live region, or
   /// is listed as not announced, with the reason. A text is one the window changes when its code-behind assigns its
   /// <c>Text</c>, <c>Message</c> or <c>Title</c> by name, or when it follows a binding. So a status text that loses
   /// its declaration fails, a new changing text fails until someone decides which it is, and a declaration on a text
   /// nothing changes fails.
   /// <para>
   /// Declaring is not announcing. The window raises the live-region event itself where it wants one (MainWindow.SetHealth
   /// does, for the footer, when the health changes). That an announcement is heard once, after the text is final, is
   /// for the interactive run. Texts that code creates (a row's cells, a dialog's guidance) are outside this test.
   /// </para>
   /// </summary>
   [Fact]
   public void StatusTexts_DeclareALiveRegion()
   {
      List<string> problems = [];
      HashSet<string> changing = new(StringComparer.Ordinal);
      foreach(SourceFile file in _markup)
      {
         IReadOnlyList<SourceFile> behind = CompanionSources.CodeBehindOf(file, _code);
         var written = behind.SelectMany(CodeBehind.TextsWritten).ToHashSet(StringComparer.Ordinal);
         var declaredInCode = behind.SelectMany(CodeBehind.LiveSettings).ToDictionary(setting => setting.Key, setting => setting.Value, StringComparer.Ordinal);
         foreach(ViewMarkup.Element element in ViewMarkup.Elements(file))
         {
            string? declared = element.LiveSetting ?? (element.Name is not null && declaredInCode.TryGetValue(element.Name, out string? inCode) ? inCode : null);
            declared = string.Equals(declared, "Off", StringComparison.Ordinal) ? null : declared;
            if(s_texts.Contains(element.Kind) && (element.Bound || (element.Name is not null && written.Contains(element.Name))))
            {
               problems.AddRange(Classify(element, declared, changing));
            }
            else if(declared is not null)
            {
               problems.Add($"{element.Where} declares a live region, and it is not a text its view changes. Remove the declaration, or change the text by the element's own name so that this test can see it.");
            }
         }
      }
      problems.AddRange(s_statusTexts.Keys.Concat(s_notAnnounced.Keys).Where(name => !changing.Contains(name)).Select(name => $"{name} is listed in this test and is no longer a text a view changes. Remove it from the list."));

      Assert.True(problems.Count == 0, Report(problems));
   }



   /// <summary>Holds one changing text to the list it is in, and reports a text that is in neither or has no name to be listed by.</summary>
   private static IEnumerable<string> Classify(ViewMarkup.Element element, string? declared, HashSet<string> changing)
   {
      string? name = element.Name ?? element.AutomationId;
      if(name is null)
      {
         return [$"{element.Where} is a text its view changes and has neither an x:Name nor an identifier. Give it one, so that it can be listed as a status text or as not announced."];
      }
      changing.Add(name);
      if(s_statusTexts.TryGetValue(name, out string? expected))
      {
         return string.Equals(declared, expected, StringComparison.Ordinal) ? []
            : [$"{element.Where} is the status text {name} and must declare AutomationProperties.LiveSetting=\"{expected}\". It declares {declared ?? "none"}."];
      }
      if(s_notAnnounced.TryGetValue(name, out string? reason))
      {
         return declared is null ? []
            : [$"{element.Where} declares a live region ({declared}), and {name} is listed as not announced: {reason} Move it to the status texts if that has changed, or remove the declaration."];
      }
      return [$"{element.Where}: {name} is a text its view changes while it is shown, and it is in neither list. If it reports status, declare AutomationProperties.LiveSetting on it (Polite, or Assertive only for an error that needs action at once) and add it to the status texts. If it does not, add it to the texts that are not announced, with the reason."];
   }



   /// <summary>The interactive kinds together with the companion's own classes that derive from one, directly or through another of its classes.</summary>
   private HashSet<string> InteractiveKinds()
   {
      HashSet<string> kinds = new(s_interactive, StringComparer.Ordinal);
      (string Name, string Base)[] classes = [.. _code.SelectMany(CodeBehind.Subclasses)];
      bool added = true;
      while(added)
      {
         // Repeated until a pass adds nothing: a class may derive from a class found in an earlier pass.
         added = false;
         foreach((string name, string parent) in classes)
         {
            added |= kinds.Contains(parent) && kinds.Add(name);
         }
      }
      return kinds;
   }



   /// <summary>Reports each control of an interactive kind that code creates without naming it in a SetAutomationId of the same file, and each exception whose count no longer matches what the file creates.</summary>
   private List<string> CreatedWithoutAnIdentifier(HashSet<string> interactive)
   {
      List<string> problems = [];
      List<(string File, string Kind)> excepted = [];
      foreach(SourceFile file in _code)
      {
         var named = CodeBehind.Identifiers(file, problems).Select(identifier => identifier.Target).ToHashSet(StringComparer.Ordinal);
         foreach(CodeBehind.Creation creation in CodeBehind.Creations(file, interactive).Where(creation => creation.Target is null || !named.Contains(creation.Target)))
         {
            if(s_createdWithoutAnIdentifier.Any(exception => string.Equals(exception.File, file.Name, StringComparison.Ordinal) && string.Equals(exception.Kind, creation.Kind, StringComparison.Ordinal)))
            {
               excepted.Add((file.Name, creation.Kind));
               continue;
            }
            problems.Add(creation.Target is null
               ? $"{creation.Where}: this {creation.Kind} is created inside another expression, so nothing can give it an identifier. Give it a variable and call SetAutomationId on it, and record the identifier in Views/{RecordedIdentifiers}."
               : $"{creation.Where}: this {creation.Kind} is created in code, and no SetAutomationId in the file names {creation.Target}. Add one, named like the others in Views/{RecordedIdentifiers}, and record it there.");
         }
      }
      foreach((string file, string kind, int count, string reason) in s_createdWithoutAnIdentifier)
      {
         int found = excepted.Count(creation => string.Equals(creation.File, file, StringComparison.Ordinal) && string.Equals(creation.Kind, kind, StringComparison.Ordinal));
         if(found != count)
         {
            problems.Add($"{file}: {count} {kind} without an identifier are excepted, and {found} were found. The exception is: {reason} Give the new one an identifier, or correct the exception.");
         }
      }
      return problems;
   }



   /// <summary>Every place an identifier is written out, in markup and in code. An identifier that follows a binding is made at run time (a tree node's position) and is not one of the recorded words.</summary>
   private List<(string Identifier, string Where)> Places(List<string> unreadable)
   {
      List<(string Identifier, string Where)> places = [.. _markup.SelectMany(ViewMarkup.Elements)
         .Where(element => !string.IsNullOrWhiteSpace(element.AutomationId) && !element.AutomationId.StartsWith('{'))
         .Select(element => (element.AutomationId!, element.Where))];
      places.AddRange(_code.SelectMany(file => CodeBehind.Identifiers(file, unreadable)).Select(identifier => (identifier.Value, identifier.Where)));
      return places;
   }



   /// <summary>Joins what a test found into one message, each finding once and on its own line.</summary>
   private static string Report(IEnumerable<string> problems) => Environment.NewLine + string.Join(Environment.NewLine, problems.Distinct(StringComparer.Ordinal));



   /// <summary>A word of letters and digits that starts with a capital letter.</summary>
   [GeneratedRegex("^[A-Z][A-Za-z0-9]*$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex Word();
}
