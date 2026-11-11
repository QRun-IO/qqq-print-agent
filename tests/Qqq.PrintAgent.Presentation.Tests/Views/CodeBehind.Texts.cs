/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.RegularExpressions;

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>
/// The texts the companion's code writes, the live regions it declares and the announcements it raises.
/// <para>
/// A text is written when a file assigns the <c>Text</c>, <c>Message</c> or <c>Title</c> of a name, in the view's own
/// code-behind or in any other file (<c>pane.ResultText.Text = ...</c> names <c>ResultText</c>); or when the name is
/// passed to a method of the companion that assigns one of those three on the parameter it arrives in
/// (<c>Show(ResultText, "Saved")</c>). A text written through a reference that carries another name (a local copy, a
/// collection, a binding source in another assembly) is not seen; the contract therefore also asks every named text to
/// be listed, whether anything is seen to write it or not.
/// </para>
/// </summary>
internal static partial class CodeBehind
{
   /// <summary>The names whose <c>Text</c>, <c>Message</c> or <c>Title</c> the files assign, directly or through a method that writes its parameter: the texts the companion changes while a view is shown.</summary>
   internal static IReadOnlySet<string> TextsWritten(IReadOnlyList<SourceFile> files)
   {
      HashSet<string> written = new(StringComparer.Ordinal);
      List<(string Method, int Position)> writers = [];
      foreach(SourceFile file in files)
      {
         written.UnionWith(TextAssignment().Matches(file.Bare).Select(match => match.Groups["name"].Value));
         IReadOnlyList<CSharpText.Method> methods = CSharpText.Methods(file.Code);
         foreach(CSharpText.Method method in methods)
         {
            var assigned = TextAssignment().Matches(CSharpText.BodyOf(file.Bare, method)).Select(match => match.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
            writers.AddRange(method.Parameters.Select((parameter, position) => (parameter, position)).Where(parameter => assigned.Contains(parameter.parameter)).Select(parameter => (method.Name, parameter.position)));
         }
      }
      foreach(SourceFile file in files)
      {
         IReadOnlyList<CSharpText.Method> methods = CSharpText.Methods(file.Code);
         foreach((string method, int position) in writers)
         {
            written.UnionWith(CSharpText.CallsOnly(file.Code, method, methods).Where(call => call.Arguments.Count > position).Select(call => LastName().Match(call.Arguments[position])).Where(match => match.Success).Select(match => match.Groups["name"].Value));
         }
      }
      return written;
   }



   /// <summary>The live regions the file declares with <c>SetLiveSetting(target, AutomationLiveSetting.Value)</c>, by target.</summary>
   internal static IReadOnlyDictionary<string, string> LiveSettings(SourceFile file)
   {
      Dictionary<string, string> settings = new(StringComparer.Ordinal);
      foreach(CSharpText.Call call in CSharpText.Calls(file.Code, "SetLiveSetting").Where(call => call.Arguments.Count == 2))
      {
         settings[call.Arguments[0]] = call.Arguments[1][(call.Arguments[1].LastIndexOf('.') + 1)..];
      }
      return settings;
   }



   /// <summary>
   /// The elements the file announces: each name in <c>FromElement(Name)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)</c>.
   /// A <c>LiveRegionChanged</c> that is raised in any other form is added to <paramref name="unreadable"/> with its place,
   /// because the contract could not then say what is announced.
   /// </summary>
   internal static IReadOnlyList<string> Announced(SourceFile file, ICollection<string> unreadable)
   {
      MatchCollection raised = RaisedLiveRegion().Matches(file.Bare);
      foreach(Match mention in LiveRegionChanged().Matches(file.Bare).Where(mention => !raised.Any(match => mention.Index >= match.Index && mention.Index < match.Index + match.Length)))
      {
         unreadable.Add($"{file.At(mention.Index)}: LiveRegionChanged is used here in a form the contract cannot read. Raise an announcement as FrameworkElementAutomationPeer.FromElement(Name)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged), with the element's own name.");
      }
      return [.. raised.Select(match => match.Groups["name"].Value)];
   }



   /// <summary>An assignment to the Text, Message or Title of a named element.</summary>
   [GeneratedRegex("""\b(?<name>[A-Za-z_]\w*)\s*\.\s*(Text|Message|Title)\s*=(?!=)""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex TextAssignment();



   /// <summary>An argument that is a name or a path of names, with its last name.</summary>
   [GeneratedRegex("""^([A-Za-z_]\w*\s*\.\s*)*(?<name>[A-Za-z_]\w*)$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex LastName();



   /// <summary><c>FromElement(Name)?.RaiseAutomationEvent(...LiveRegionChanged)</c>.</summary>
   [GeneratedRegex("""\bFromElement\s*\(\s*(?<name>[A-Za-z_]\w*)\s*\)\s*\??\s*\.\s*RaiseAutomationEvent\s*\(\s*[\w.\s]*\bLiveRegionChanged\s*\)""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex RaisedLiveRegion();



   /// <summary>The event's name wherever it stands.</summary>
   [GeneratedRegex("""\bLiveRegionChanged\b""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex LiveRegionChanged();
}
