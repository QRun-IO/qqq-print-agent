/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>The texts a view shows by name: which are status texts with a live region, which change and are not announced, and which never change.</summary>
public sealed partial class XamlContractTests
{
   private const string FooterSaysIt = "It says in more detail what the footer's status line says. The footer is the one live region for the state of the connection, so a change is announced once.";
   private const string MessageBarSaysIt = "It shows the state of Windows as last read. The result of the operator's action is announced by the message bar, and this text is read on demand.";
   private const string NeverQuiet = "It changes with every contact with the server, or every second. A live region here would never be quiet.";
   private const string DescribesAList = "It describes the list on its page and follows every refresh and every filter keystroke. The rows are what the operator reads.";
   private const string NamesSomething = "It names or describes something and reports no result: a heading, a label, or a detail that is read on demand.";
   private const string ToBeDecided = "Not announced today; to be decided in the view step (audit U28, with U9). ";

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

   /// <summary>
   /// The texts the window changes that are not live regions, by <c>x:Name</c>, each with the reason. Three of them
   /// carry no reason for staying silent, only the fact: an operator who cannot see the screen does not hear them
   /// today, and whether they should be heard is decided where the views are rewritten.
   /// </summary>
   private static readonly Dictionary<string, string> s_notAnnounced = new(StringComparer.Ordinal)
   {
      ["ConnectionFeedback"] = ToBeDecided + "It is the connection dialog's InfoBar and declares no live region. WinUI announces an InfoBar when it opens and says nothing when an open one changes (InfoBar guidance, \"Updating the InfoBar\"). This one cannot be closed (IsClosable=\"False\", MainWindow.xaml:140) and the window never closes it (Feedback only opens it, MainWindow.xaml.cs:414), so after its first opening a new result in it is announced by nothing. It goes with the connection dialog (ADR-0005).",
      ["StatusText"] = FooterSaysIt,
      ["ConnectionText"] = FooterSaysIt,
      ["AuthenticationText"] = FooterSaysIt,
      ["TransportStateText"] = FooterSaysIt,
      ["FooterContact"] = NeverQuiet,
      ["TransportTrafficText"] = NeverQuiet,
      ["ServiceStateText"] = MessageBarSaysIt,
      ["ServiceGuidanceText"] = MessageBarSaysIt,
      ["StartupGuidanceText"] = ToBeDecided + "It is the only place the result of the sign-in startup switch is written (MainWindow.Actions.cs:229). The message bar is used only when the change throws (:233), and the switch is then put back to the saved state (:237), so this text is the only explanation of a switch that moved back.",
      ["PrintersSummary"] = DescribesAList,
      ["PrintersEmptyText"] = DescribesAList,
      ["JobsSummary"] = ToBeDecided + "It is the search's result count, and it is where \"History unavailable; check the service and refresh\" and the note that the rows shown may be stale are written (MainWindow.Jobs.cs:100 and :114). When the service cannot be reached nothing else reports it.",
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

   /// <summary>The named texts that never change while their view is shown, by <c>x:Name</c>, each with what it is. A text is named for other reasons than being written: to be hidden and shown, or to be found.</summary>
   private static readonly Dictionary<string, string> s_neverChanges = new(StringComparer.Ordinal)
   {
      ["PrintersHint"] = "The fixed hint under the printer list. Its words are in the markup, and no code names it.",
   };

   /// <summary>
   /// The announcements the window makes, by the <c>x:Name</c> of the text and the file that raises the event.
   /// Declaring a live region is not announcing: WinUI raises nothing when a TextBlock's text changes, so a text is
   /// heard only where the window raises <c>LiveRegionChanged</c> for it. There is one such place, for the footer's
   /// status line, when the health changes.
   /// </summary>
   private static readonly Dictionary<string, string> s_announced = new(StringComparer.Ordinal)
   {
      ["FooterStatus"] = "MainWindow.xaml.cs",
   };



   /// <summary>
   /// Every text a view names is a status text, and then declares its live region; or is listed as changing without
   /// being announced, with the reason; or is listed as never changing. A text changes when any file of the companion
   /// assigns its <c>Text</c>, <c>Message</c> or <c>Title</c> by name, directly or through a method that writes its
   /// parameter, or when it follows a binding (an <c>{x:Bind}</c> without a mode follows one where
   /// <c>x:DefaultBindMode</c> says so). So a status text that loses its declaration fails; a new named text fails
   /// until someone decides which it is; a text listed as never changing fails once something is seen to change it;
   /// a text that changes and has no name to be listed by fails; and a declaration on anything else fails.
   /// <para>
   /// Declaring is not announcing (see <c>TheAnnouncementsTheWindowMakes_AreStillMade</c>). That an announcement is
   /// heard once, after the text is final, is for the interactive run. Texts that code creates (a row's cells, a
   /// dialog's guidance) are outside this test.
   /// </para>
   /// </summary>
   [Fact]
   public void StatusTexts_DeclareALiveRegion()
   {
      List<string> problems = [];
      HashSet<string> seen = new(StringComparer.Ordinal);
      IReadOnlySet<string> written = CodeBehind.TextsWritten(_code);
      foreach(SourceFile file in _markup)
      {
         var declaredInCode = CompanionSources.CodeBehindOf(file, _code).SelectMany(CodeBehind.LiveSettings).ToDictionary(setting => setting.Key, setting => setting.Value, StringComparer.Ordinal);
         foreach(ViewMarkup.Element element in ViewMarkup.Elements(file))
         {
            string? declared = element.LiveSetting ?? (element.Name is not null && declaredInCode.TryGetValue(element.Name, out string? inCode) ? inCode : null);
            declared = string.Equals(declared, "Off", StringComparison.Ordinal) ? null : declared;
            bool changes = element.Bound || (element.Name is not null && written.Contains(element.Name));
            if(s_texts.Contains(element.Kind) && (element.Name is not null || changes))
            {
               problems.AddRange(Classify(element, declared, changes, seen));
            }
            else if(declared is not null)
            {
               problems.Add($"{element.Where} declares a live region, and it is not a text the contract can list: a live region belongs on a TextBlock, RichTextBlock or InfoBar that has an x:Name. Remove the declaration, or give the text a name and list it as a status text.");
            }
         }
      }
      problems.AddRange(s_statusTexts.Keys.Concat(s_notAnnounced.Keys).Concat(s_neverChanges.Keys).Where(name => !seen.Contains(name)).Select(name => $"{name} is listed in this test and no view has a text of that name any more. Remove it from the list."));

      Assert.True(problems.Count == 0, Report(problems));
   }



   /// <summary>
   /// The announcements the window makes are still made: each text listed as announced has, in the file named, the
   /// call that raises <c>LiveRegionChanged</c> for it, and it is a status text, so the event has a live region to
   /// speak for. A raise for a text that is not listed fails too, so that a new announcement is a line here. This
   /// holds the call as text; that it is heard, once and after the text is final, is for the interactive run.
   /// </summary>
   [Fact]
   public void TheAnnouncementsTheWindowMakes_AreStillMade()
   {
      List<string> problems = [];
      (string Name, string File)[] raised = [.. _code.SelectMany(file => CodeBehind.Announced(file, problems).Select(name => (name, file.Name)))];
      problems.AddRange(s_announced.Where(announced => !raised.Contains((announced.Key, announced.Value)))
         .Select(announced => $"{announced.Key} is listed as announced, and {announced.Value} no longer raises LiveRegionChanged for it. Its live region is then declared and never spoken. Keep FrameworkElementAutomationPeer.FromElement({announced.Key})?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged) where the text changes; if the announcement moved to another file, change the entry."));
      problems.AddRange(raised.Where(found => !s_announced.TryGetValue(found.Name, out string? file) || !string.Equals(file, found.File, StringComparison.Ordinal))
         .Select(found => $"{found.File} raises LiveRegionChanged for {found.Name}, and that announcement is not listed. Add it to the announcements in this test."));
      problems.AddRange(s_announced.Keys.Where(name => !s_statusTexts.ContainsKey(name)).Select(name => $"{name} is announced and is not a status text, so it declares no live region for the event to speak for. List it as a status text and declare AutomationProperties.LiveSetting on it."));

      Assert.True(problems.Count == 0, Report(problems));
   }



   /// <summary>Holds one text to the list it is in, and reports a text that is in none or has no name to be listed by.</summary>
   private static IEnumerable<string> Classify(ViewMarkup.Element element, string? declared, bool changes, HashSet<string> seen)
   {
      if(element.Name is null)
      {
         return [$"{element.Where} is a text its view changes and has no x:Name. Give it one, so that it can be listed as a status text or as not announced."];
      }
      string name = element.Name;
      seen.Add(name);
      if(s_statusTexts.TryGetValue(name, out string? expected))
      {
         return string.Equals(declared, expected, StringComparison.Ordinal) ? Unchanging(element, name, changes)
            : [$"{element.Where} is the status text {name} and must declare AutomationProperties.LiveSetting=\"{expected}\". It declares {declared ?? "none"}."];
      }
      if(s_notAnnounced.TryGetValue(name, out string? reason))
      {
         return declared is null ? Unchanging(element, name, changes)
            : [$"{element.Where} declares a live region ({declared}), and {name} is listed as not announced: {reason} Move it to the status texts if that has changed, or remove the declaration."];
      }
      if(!s_neverChanges.ContainsKey(name))
      {
         return [$"{element.Where}: {name} is a named text, and it is in none of the three lists. If it reports status, declare AutomationProperties.LiveSetting on it (Polite, or Assertive only for an error that needs action at once) and add it to the status texts. If the window changes it and it is not to be announced, add it to the texts that are not announced, with the reason. If nothing ever changes it, add it to the texts that never change."];
      }
      if(declared is not null || changes)
      {
         return [$"{element.Where}: {name} is listed as a text that never changes, and {(changes ? "the companion now changes it (an assignment to it, a method that writes it, or a binding)" : "it declares a live region")}. Decide what it is: a status text, which declares AutomationProperties.LiveSetting, or a text that is not announced, with the reason."];
      }
      return [];
   }



   /// <summary>Reports a text that is listed as changing and that nothing the contract reads changes, so that the lists stay what they say.</summary>
   private static IEnumerable<string> Unchanging(ViewMarkup.Element element, string name, bool changes)
      => changes ? [] : [$"{element.Where}: {name} is listed as a text the window changes, and nothing the contract reads changes it (no assignment to it, no method that writes it, no binding). Move it to the texts that never change, or change it by its own name so that this test can see it."];
}
