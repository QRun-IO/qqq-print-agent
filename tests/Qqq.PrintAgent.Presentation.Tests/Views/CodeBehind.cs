/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.RegularExpressions;

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>
/// What the C# of the companion does to controls, read from its text: the identifiers it sets (this file), the
/// controls it creates (CodeBehind.Creations.cs), and the texts it writes, the live regions it declares and the
/// announcements it raises (CodeBehind.Texts.cs).
/// <para>
/// An identifier is set by <c>SetAutomationId(target, value)</c> and in no other way. The value is a string literal;
/// a choice between two literals; or a parameter of the method the call is in, and then every call of that method, in
/// any file of the companion, passes a literal there. Any other value is an identifier that is not written out. It is
/// returned as such with its place, and the contract allows it only at a place its table of generated forms names.
/// </para>
/// <para>
/// A mention of an automation identifier outside such a call (<c>AutomationIdProperty</c> with <c>SetValue</c>, the
/// method handed over as a delegate) and markup loaded from a string (<c>XamlReader</c>) are reported with what to
/// write instead: either could set an identifier, or make a control, that nothing here reads. A call of
/// <c>GetAutomationId</c> reads an identifier and sets none, and is left alone.
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
   /// file declares, and the control is the target; the target and the side of a choice; or the target and the call
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
            List<Identifier>? read = ReadOne(files, methods, index, call);
            if(read is null)
            {
               notWrittenOut.Add((file.At(call.Index), $"{OwnerOf(file)} | {TargetOf(call)} | {Flat(call.Arguments[1])}"));
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



   /// <summary>Reads the identifier, or identifiers, one call sets; null when the value is none of the three forms the class comment names.</summary>
   private static List<Identifier>? ReadOne(IReadOnlyList<SourceFile> files, IReadOnlyList<CSharpText.Method>[] methods, int index, CSharpText.Call call)
   {
      SourceFile file = files[index];
      string target = TargetOf(call);
      string value = call.Arguments[1];
      if(CSharpText.TryReadLiteral(value, out string literal))
      {
         return [new Identifier(file.At(call.Index), OwnerOf(file), target, literal, target)];
      }
      Match choice = ChoiceOfTwoLiterals().Match(value);
      if(choice.Success)
      {
         string condition = Flat(choice.Groups["condition"].Value);
         return
         [
            new Identifier(file.At(call.Index), OwnerOf(file), target, choice.Groups["first"].Value, $"{target}, when {condition}"),
            new Identifier(file.At(call.Index), OwnerOf(file), target, choice.Groups["second"].Value, $"{target}, unless {condition}"),
         ];
      }
      CSharpText.Method? method = CSharpText.Enclosing(methods[index], call.Index);
      int position = method is null ? -1 : method.Parameters.ToList().IndexOf(value);
      return method is null || position < 0 ? null : Forwarded(files, methods, method.Name, position, target);
   }



   /// <summary>
   /// Reads the identifiers passed to a helper that forwards its parameter to <c>SetAutomationId</c>: one from each call
   /// of that name in any file. Null when there is no call, or when one passes something else than a literal. A call
   /// is found by the helper's name alone, so a helper named like another method is reported rather than half read.
   /// </summary>
   private static List<Identifier>? Forwarded(IReadOnlyList<SourceFile> files, IReadOnlyList<CSharpText.Method>[] methods, string helper, int position, string target)
   {
      List<Identifier> passed = [];
      for(int index = 0; index < files.Count; index++)
      {
         foreach(CSharpText.Call caller in CSharpText.CallsOnly(files[index].Code, helper, methods[index]))
         {
            if(caller.Arguments.Count <= position || !CSharpText.TryReadLiteral(caller.Arguments[position], out string argument))
            {
               return null;
            }
            IEnumerable<string> others = caller.Arguments.Where((other, at) => at != position && CSharpText.TryReadLiteral(other, out _));
            passed.Add(new Identifier(files[index].At(caller.Index), OwnerOf(files[index]), target, argument, $"{target} in {helper}({string.Join(", ", others)})"));
         }
      }
      return passed.Count == 0 ? null : passed;
   }



   /// <summary>Reports each mention of an automation identifier that is not a <c>SetAutomationId</c> or <c>GetAutomationId</c> call or inside one, and each use of <c>XamlReader</c>, with what to write instead.</summary>
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



   /// <summary>The class a file's controls belong to: the first type the file declares, which for a file of a partial class is that class; the file's name when it declares none.</summary>
   internal static string OwnerOf(SourceFile file) => Types(file).Select(type => type.Name).FirstOrDefault() ?? file.Name;



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



   /// <summary>A name that carries an automation identifier, or the reader of markup from a string.</summary>
   [GeneratedRegex("""AutomationId|\bXamlReader\b""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex WhatTheReaderCannotFollow();



   /// <summary>A run of white space.</summary>
   [GeneratedRegex("""\s+""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex WhiteSpace();
}
