/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.RegularExpressions;

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>
/// What a C# file of the companion does to controls, read from its text: the identifiers it sets, the controls it
/// creates, the texts it writes and the live regions it declares.
/// <para>
/// What it can see, which is how the companion writes these things today. An identifier is set by
/// <c>SetAutomationId(target, value)</c>, where the value is a string literal, a choice between two literals, or a
/// parameter of the method the call is in whose every caller in the same file passes a literal. A control is created
/// by <c>new Kind</c>, by <c>Kind name = new(</c> (a local, a field or a property), or by a method written as
/// <c>Kind Name(...) => new(</c>.
/// </para>
/// <para>
/// What it cannot see: a control created by a <c>new()</c> whose type is written somewhere else (a <c>return new()</c>
/// in a method with a block body, an argument, an item of a collection expression), and a text of a view that is
/// written through another reference than the element's own name. The companion creates no control of an interactive
/// kind that way today, and writes no text of a view that way. An identifier it cannot read is reported, never
/// ignored.
/// </para>
/// </summary>
internal static partial class CodeBehind
{
   /// <summary>One identifier and the place it is written. <c>Target</c> is the first argument of the call that sets it: a field, a local, a property, or <c>this</c>.</summary>
   internal sealed record Identifier(string Where, string Target, string Value);



   /// <summary>One control created in code. <c>Target</c> is the variable, field or property it is assigned to, null when it is created inside another expression.</summary>
   internal sealed record Creation(string Where, string Kind, string? Target);



   /// <summary>Every identifier the file sets. A call whose identifier cannot be read is added to <paramref name="unreadable"/> with its place.</summary>
   internal static IReadOnlyList<Identifier> Identifiers(SourceFile file, ICollection<string> unreadable)
   {
      List<Identifier> identifiers = [];
      IReadOnlyList<CSharpText.Method> methods = CSharpText.Methods(file.Code);
      foreach(CSharpText.Call call in CSharpText.Calls(file.Code, "SetAutomationId"))
      {
         List<Identifier>? read = call.Arguments.Count == 2 ? Read(file, methods, call) : null;
         if(read is null)
         {
            unreadable.Add($"{file.At(call.Index)}: the identifier in SetAutomationId({string.Join(", ", call.Arguments)}) cannot be read. Write it as a string literal, or pass it as a parameter from callers in this file that each write a literal.");
            continue;
         }
         identifiers.AddRange(read);
      }
      return identifiers;
   }



   /// <summary>Every creation in the file of a kind in the given set.</summary>
   internal static IReadOnlyList<Creation> Creations(SourceFile file, IReadOnlySet<string> kinds)
   {
      IEnumerable<Match> matches = NewOfANamedType().Matches(file.Code).Concat(NewOfTheDeclaredType().Matches(file.Code)).Concat(MethodReturningNew().Matches(file.Code));
      return [.. matches.Where(match => kinds.Contains(match.Groups["kind"].Value)).OrderBy(match => match.Index)
         .Select(match => new Creation(file.At(match.Groups["kind"].Index), match.Groups["kind"].Value, match.Groups["target"].Success ? match.Groups["target"].Value : null))];
   }



   /// <summary>The classes the file declares, each with the type it derives from, so a control derived from an interactive kind is held to the same rule as its base.</summary>
   internal static IReadOnlyList<(string Name, string Base)> Subclasses(SourceFile file)
      => [.. ClassWithABase().Matches(file.Code).Select(match => (match.Groups["name"].Value, match.Groups["base"].Value))];



   /// <summary>The names whose <c>Text</c>, <c>Message</c> or <c>Title</c> the file assigns: the texts it changes while the view is shown.</summary>
   internal static IReadOnlySet<string> TextsWritten(SourceFile file)
      => TextAssignment().Matches(file.Code).Select(match => match.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);



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



   /// <summary>Reads the identifier, or identifiers, one call sets; null when the value is none of the three forms the class comment names.</summary>
   private static List<Identifier>? Read(SourceFile file, IReadOnlyList<CSharpText.Method> methods, CSharpText.Call call)
   {
      string target = call.Arguments[0];
      string value = call.Arguments[1];
      if(CSharpText.TryReadLiteral(value, out string literal))
      {
         return [new Identifier(file.At(call.Index), target, literal)];
      }
      Match choice = ChoiceOfTwoLiterals().Match(value);
      if(choice.Success)
      {
         return [new Identifier(file.At(call.Index), target, choice.Groups["first"].Value), new Identifier(file.At(call.Index), target, choice.Groups["second"].Value)];
      }
      CSharpText.Method? method = CSharpText.Enclosing(methods, call.Index);
      int position = method is null ? -1 : method.Parameters.ToList().IndexOf(value);
      if(method is null || position < 0)
      {
         return null;
      }
      List<Identifier> passed = [];
      foreach(CSharpText.Call caller in CSharpText.Calls(file.Code, method.Name).Where(caller => caller.Index != method.Index))
      {
         if(caller.Arguments.Count <= position || !CSharpText.TryReadLiteral(caller.Arguments[position], out string argument))
         {
            return null;
         }
         passed.Add(new Identifier(file.At(caller.Index), target, argument));
      }
      return passed.Count == 0 ? null : passed;
   }



   /// <summary><c>new Kind</c>, with the name it is assigned to when it is assigned directly. An array or a generic type is not a control.</summary>
   [GeneratedRegex("""((?<target>\b[A-Za-z_]\w*)\s*=\s*)?\bnew\s+([A-Za-z_]\w*\.)*(?<kind>[A-Z]\w*)\b(?!\s*[\[<.])""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex NewOfANamedType();



   /// <summary><c>Kind name = new(</c>: a local, a field, or a property with an initializer.</summary>
   [GeneratedRegex("""\b(?<kind>[A-Z]\w*)\s+(?<target>[A-Za-z_]\w*)\s*(\{\s*get;[^{}]*\}\s*)?=\s*new\s*\(""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex NewOfTheDeclaredType();



   /// <summary><c>Kind Name(...) => new(</c>: a method whose whole body creates its return type. What it returns has no name here.</summary>
   [GeneratedRegex("""\b(?<kind>[A-Z]\w*)\s+[A-Za-z_]\w*\s*\([^()]*\)\s*=>\s*new\s*\(""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex MethodReturningNew();



   /// <summary>A class declaration with the first type after its colon.</summary>
   [GeneratedRegex("""\bclass\s+(?<name>[A-Za-z_]\w*)[^:{;]*:\s*([A-Za-z_]\w*\.)*(?<base>[A-Z]\w*)""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex ClassWithABase();



   /// <summary>An assignment to the Text, Message or Title of a named element.</summary>
   [GeneratedRegex("""\b(?<name>[A-Za-z_]\w*)\.(Text|Message|Title)\s*=(?!=)""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex TextAssignment();



   /// <summary><c>condition ? "One" : "Other"</c>.</summary>
   [GeneratedRegex("""^[^?"]+\?\s*"(?<first>[^"\\]*)"\s*:\s*"(?<second>[^"\\]*)"$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex ChoiceOfTwoLiterals();
}
