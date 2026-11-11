/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.RegularExpressions;

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>
/// What the C# of the companion does to controls, read from its text: the identifiers it sets (this file), the
/// controls it creates (CodeBehind.Creations.cs), the names it gives while it runs (CodeBehind.Names.cs), and the
/// texts it writes, the live regions it declares and the announcements it raises (CodeBehind.Texts.cs).
/// <para>
/// An identifier is set by <c>SetAutomationId(target, value)</c> and in no other way. The value is a string literal;
/// a choice between two literals; or a parameter of the method the call is in, and then every call of that method, in
/// any file of the companion, passes a literal there. Any other value is an identifier that is not written out. It is
/// returned as such with its place, and the contract allows it only at a place its table of generated forms names.
/// A call of such a method that passes something else than a literal is reported at the call, because the calls are
/// found by the method's name alone: it is either an identifier that is not written out, or a call of another method
/// of the same name, and the report says what to do in each case.
/// </para>
/// <para>
/// A mention of an automation identifier outside such a call (<c>AutomationIdProperty</c> with <c>SetValue</c>, the
/// method handed over as a delegate), an element's name set through <c>NameProperty</c>, and markup loaded from a
/// string (<c>XamlReader</c>) are reported with what to write instead: each could set an identifier, or make a
/// control, that nothing here reads. A call of <c>GetAutomationId</c> reads an identifier and sets none, and is left
/// alone.
/// </para>
/// </summary>
internal static partial class CodeBehind
{
   private const string Setter = "SetAutomationId";
   private const string Getter = "GetAutomationId";



   /// <summary>
   /// One identifier and the place it is written. <c>Target</c> is the first argument of the call that sets it: a field,
   /// a local, a property, or <c>this</c>. <c>Owner</c> and <c>Control</c> say which control that is without a file
   /// name or a line number, so that it can be recorded and a renamed file changes nothing: the owner is the class the
   /// line is in, and the control is the target; the target and the side of a choice; or the target and the call
   /// that forwarded the identifier, with the other literals of that call.
   /// </summary>
   internal sealed record Identifier(string Where, string Owner, string Target, string Value, string Control);



   /// <summary>
   /// What the companion's code says about identifiers: the ones written out; the places where one is set that is not
   /// written out, each as "class | target | expression"; and what could not be read at all, each with what to write
   /// instead.
   /// </summary>
   internal sealed record Reading(IReadOnlyList<Identifier> Identifiers, IReadOnlyList<(string Where, string Place)> NotWrittenOut, IReadOnlyList<string> Unreadable);



   /// <summary>Reads every identifier the files set. All the files are given at once, because a helper that forwards an identifier may be called from another file.</summary>
   internal static Reading Read(IReadOnlyList<SourceFile> files)
   {
      List<Identifier> identifiers = [];
      List<(string Where, string Place)> notWrittenOut = [];
      List<string> unreadable = [];
      IReadOnlyList<CSharpText.Method>[] methods = [.. files.Select(file => CSharpText.Methods(file.Code))];
      for(int index = 0; index < files.Count; index++)
      {
         SourceFile file = files[index];
         IReadOnlyList<CSharpText.Call> calls = CSharpText.Calls(file.Code, Setter);
         foreach(CSharpText.Call call in calls)
         {
            if(call.Arguments.Count != 2)
            {
               unreadable.Add($"{file.At(call.Index)}: SetAutomationId({Flat(string.Join(", ", call.Arguments))}) does not take a control and an identifier, so the identifier cannot be read. Write AutomationProperties.SetAutomationId(control, \"Identifier\").");
               continue;
            }
            List<Identifier>? read = ReadOne(files, methods, index, call, unreadable);
            if(read is null)
            {
               notWrittenOut.Add((file.At(call.Index), $"{OwnerOf(file, call.Index)} | {TargetOf(call)} | {Flat(call.Arguments[1])}"));
               continue;
            }
            identifiers.AddRange(read);
         }
         unreadable.AddRange(Mentions(file, [.. calls, .. CSharpText.Calls(file.Code, Getter)]));
      }
      return new Reading(identifiers, notWrittenOut, unreadable);
   }



   /// <summary>What the file's <c>SetAutomationId</c> calls name, whatever the identifier: each target with where the call is, so that a control created in code can be matched to the call that identifies it.</summary>
   internal static IReadOnlyList<(string Target, int Index)> Named(SourceFile file)
      => [.. CSharpText.Calls(file.Code, Setter).Where(call => call.Arguments.Count > 0).Select(call => (TargetOf(call), call.Index))];



   /// <summary>Reads the identifier, or identifiers, one call sets; null when the value is none of the three forms the class comment names. A call of a forwarding helper that passes no literal is added to <paramref name="unreadable"/>.</summary>
   private static List<Identifier>? ReadOne(IReadOnlyList<SourceFile> files, IReadOnlyList<CSharpText.Method>[] methods, int index, CSharpText.Call call, List<string> unreadable)
   {
      SourceFile file = files[index];
      string target = TargetOf(call);
      string value = call.Arguments[1];
      string owner = OwnerOf(file, call.Index);
      if(CSharpText.TryReadLiteral(value, out string literal))
      {
         return [new Identifier(file.At(call.Index), owner, target, literal, target)];
      }
      Match choice = ChoiceOfTwoLiterals().Match(value);
      if(choice.Success)
      {
         string condition = Flat(choice.Groups["condition"].Value);
         return
         [
            new Identifier(file.At(call.Index), owner, target, choice.Groups["first"].Value, $"{target}, when {condition}"),
            new Identifier(file.At(call.Index), owner, target, choice.Groups["second"].Value, $"{target}, unless {condition}"),
         ];
      }
      CSharpText.Method? method = CSharpText.Enclosing(methods[index], call.Index);
      int position = method is null ? -1 : method.Parameters.ToList().IndexOf(value);
      return method is null || position < 0 ? null : Forwarded(files, methods, (method.Name, position, file.At(call.Index)), target, unreadable);
   }



   /// <summary>
   /// Reads the identifiers passed to a helper that forwards its parameter to <c>SetAutomationId</c>: one from each call
   /// of that name in any file. Null when nothing calls it. A call is found by the helper's name alone, so a call that
   /// passes no literal at the helper's position may be a call of another method of that name. It is added to
   /// <paramref name="unreadable"/> with its own place and both ways out, and the calls that do pass a literal are
   /// still read, so that the one cause is reported once and not a second time as identifiers that are no longer set.
   /// </summary>
   private static List<Identifier>? Forwarded(IReadOnlyList<SourceFile> files, IReadOnlyList<CSharpText.Method>[] methods, (string Name, int Position, string SetAt) helper, string target, List<string> unreadable)
   {
      List<Identifier> passed = [];
      bool called = false;
      for(int index = 0; index < files.Count; index++)
      {
         foreach(CSharpText.Call caller in CSharpText.CallsOnly(files[index].Code, helper.Name, methods[index]))
         {
            called = true;
            if(caller.Arguments.Count <= helper.Position || !CSharpText.TryReadLiteral(caller.Arguments[helper.Position], out string argument))
            {
               unreadable.Add($"{files[index].At(caller.Index)}: {Flat($"{helper.Name}({string.Join(", ", caller.Arguments)})")} does not pass a string literal as argument {helper.Position + 1}, and a method called {helper.Name} forwards that argument to SetAutomationId at {helper.SetAt}. The contract finds the calls of such a helper by its name alone, in every file of the companion. If this is a call of that helper, write the identifier here as a string literal. If it is a call of another method that is also called {helper.Name}, rename one of the two: a helper that forwards an identifier needs a name no other method in the companion has.");
               continue;
            }
            IEnumerable<string> others = caller.Arguments.Where((other, at) => at != helper.Position && CSharpText.TryReadLiteral(other, out _));
            passed.Add(new Identifier(files[index].At(caller.Index), OwnerOf(files[index], caller.Index), target, argument, $"{target} in {helper.Name}({string.Join(", ", others)})"));
         }
      }
      return called ? passed : null;
   }



   /// <summary>Reports each mention of an automation identifier that is not a <c>SetAutomationId</c> or <c>GetAutomationId</c> call or inside one, and each use of <c>XamlReader</c> or <c>NameProperty</c>, with what to write instead.</summary>
   private static IEnumerable<string> Mentions(SourceFile file, IReadOnlyList<CSharpText.Call> calls)
   {
      foreach(Match match in WhatTheReaderCannotFollow().Matches(file.Bare))
      {
         if(calls.Any(call => match.Index >= call.Index && match.Index <= call.Close))
         {
            continue;
         }
         string word = CSharpText.WordAt(file.Bare, match.Index);
         if(string.Equals(word, "XamlReader", StringComparison.Ordinal))
         {
            yield return $"{file.At(match.Index)}: XamlReader builds controls from markup in a string, and the contract does not read that markup. Put the markup in a view's .xaml file, or create the control in code with new Kind, so that its identifier can be held.";
         }
         else if(string.Equals(word, "NameProperty", StringComparison.Ordinal))
         {
            yield return $"{file.At(match.Index)}: an element's name set through NameProperty cannot be read by the contract, and WinUI reports an element's name as its automation identifier when it has no other. Name the element with x:Name in markup, or give it an identifier with AutomationProperties.SetAutomationId(control, \"Identifier\").";
         }
         else if(word.Contains("AutomationIdProperty", StringComparison.Ordinal))
         {
            yield return $"{file.At(match.Index)}: an identifier set or read through {word} cannot be read by the contract. Write AutomationProperties.SetAutomationId(control, \"Identifier\") instead of SetValue(AutomationProperties.AutomationIdProperty, ...).";
         }
         else
         {
            yield return $"{file.At(match.Index)}: {word} is mentioned outside a call of SetAutomationId, and the contract reads identifiers only from that call. Write AutomationProperties.SetAutomationId(control, \"Identifier\") where the identifier is set, and name an automation identifier nowhere else.";
         }
      }
   }



   /// <summary>
   /// The class the control at a position belongs to: the innermost type whose body encloses the position, so that a
   /// record declared above a class, or a class nested in it, owns only its own lines. Where no type's body encloses
   /// it (a file of statements), it is the first type the file declares, and the file's name when it declares none.
   /// </summary>
   internal static string OwnerOf(SourceFile file, int index)
   {
      string? owner = null;
      foreach(Match type in TypeDeclaration().Matches(file.Bare).Where(type => type.Index < index))
      {
         int body = file.Bare.IndexOfAny(['{', ';'], type.Index + type.Length);
         if(body >= 0 && file.Bare[body] == '{' && index < CSharpText.BlockAround(file.Bare, body + 1).Close)
         {
            owner = type.Groups["name"].Value;
         }
      }
      return owner ?? Types(file).Select(type => type.Name).FirstOrDefault() ?? file.Name;
   }



   /// <summary>The first argument of a call as one line, without a leading <c>this.</c>: a field is the same control however it is written.</summary>
   private static string TargetOf(CSharpText.Call call)
   {
      string target = Flat(call.Arguments[0]);
      return target.StartsWith("this.", StringComparison.Ordinal) ? target["this.".Length..] : target;
   }



   /// <summary>The text on one line: every run of white space, line breaks included, becomes one space.</summary>
   private static string Flat(string text) => WhiteSpace().Replace(text.Trim(), " ");



   /// <summary><c>condition ? "One" : "Other"</c>.</summary>
   [GeneratedRegex("""^(?<condition>[^?"]+)\?\s*"(?<first>[^"\\]*)"\s*:\s*"(?<second>[^"\\]*)"$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex ChoiceOfTwoLiterals();



   /// <summary>A name that carries an automation identifier, the reader of markup from a string, or the property an element's name is set through.</summary>
   [GeneratedRegex("""AutomationId|\bXamlReader\b|\bNameProperty\b""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex WhatTheReaderCannotFollow();



   /// <summary>A run of white space.</summary>
   [GeneratedRegex("""\s+""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex WhiteSpace();
}
