/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.RegularExpressions;

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>
/// What the two interactive suites look up, read from their source. The UI tests (FlaUI, against the built companion)
/// and the layout harness (the real views over a synthetic service) are not in the gate, so nothing else fails when a
/// view stops carrying what they look for.
/// <para>
/// An automation identifier is the argument, at the position given below, of one of the suites' own lookups, or the
/// literal an identifier read with <c>GetAutomationId</c> is compared with. The argument is a string literal; or the
/// variable of a <c>foreach</c> over a <c>string[]</c> of literals; or a parameter of the helper the call is in,
/// which must then be a lookup itself at that position, so that its callers are read in turn. Anything else is
/// reported: a lookup this cannot follow is never skipped.
/// </para>
/// </summary>
internal static partial class InteractiveSuites
{
   private static readonly Dictionary<string, int> s_lookups = new(StringComparer.Ordinal)
   {
      ["WaitForElement"] = 0,
      ["WaitForElementGone"] = 0,
      ["ClickElement"] = 0,
      ["MouseClickElement"] = 0,
      ["EnterText"] = 0,
      ["ByAutomationId"] = 0,
      ["InvokeMenuItem"] = 1,
      ["OpenMenuAndFindItem"] = 1,
      ["Navigate"] = 1,
   };



   /// <summary>Every automation identifier the suites look up, in order. A lookup whose identifier cannot be read is added to <paramref name="unreadable"/> with its place.</summary>
   internal static SortedSet<string> IdentifiersLookedUp(IEnumerable<SourceFile> files, ICollection<string> unreadable)
   {
      SortedSet<string> identifiers = new(StringComparer.Ordinal);
      foreach(SourceFile file in files)
      {
         IReadOnlyList<CSharpText.Method> methods = CSharpText.Methods(file.Code);
         foreach((int index, string argument) in Lookups(file, methods))
         {
            List<string>? read = Read(file, methods, index, argument);
            if(read is null)
            {
               unreadable.Add($"{file.At(index)}: the identifier looked up here ({argument}) cannot be read. Write it as a string literal, or add the helper that forwards it to the lookups in InteractiveSuites.");
               continue;
            }
            identifiers.UnionWith(read);
         }
      }
      return identifiers;
   }



   /// <summary>Every element name the suites look up with <c>FindName("Name")</c> or read as a field of the window with <c>Field&lt;T&gt;(window, "Name")</c>, in order.</summary>
   internal static SortedSet<string> ElementNamesLookedUp(IEnumerable<SourceFile> files)
   {
      SortedSet<string> names = new(StringComparer.Ordinal);
      foreach(SourceFile file in files)
      {
         names.UnionWith(NameLookup().Matches(file.Code).Select(match => match.Groups["name"].Value));
      }
      return names;
   }



   /// <summary>The places an identifier is looked up in one file, each with the argument that carries it. A helper's own declaration is not a lookup.</summary>
   private static IEnumerable<(int Index, string Argument)> Lookups(SourceFile file, IReadOnlyList<CSharpText.Method> methods)
   {
      foreach((string name, int position) in s_lookups)
      {
         foreach(CSharpText.Call call in CSharpText.Calls(file.Code, name).Where(call => !methods.Any(method => method.Index == call.Index)))
         {
            yield return (call.Index, call.Arguments.Count > position ? call.Arguments[position] : string.Empty);
         }
      }
      foreach(Match match in ComparedIdentifier().Matches(file.Code))
      {
         yield return (match.Index, match.Groups["value"].Value);
      }
   }



   /// <summary>Reads the identifiers one lookup's argument stands for: none when it is a helper's own parameter (its callers carry them), null when it cannot be read.</summary>
   private static List<string>? Read(SourceFile file, IReadOnlyList<CSharpText.Method> methods, int index, string argument)
   {
      if(CSharpText.TryReadLiteral(argument, out string literal))
      {
         return [literal];
      }
      CSharpText.Method? method = CSharpText.Enclosing(methods, index);
      if(method is null)
      {
         return null;
      }
      int position = method.Parameters.ToList().IndexOf(argument);
      if(position >= 0)
      {
         return s_lookups.TryGetValue(method.Name, out int forwarded) && forwarded == position ? [] : null;
      }
      return LoopedOver(file.Code[method.Index..index], argument);
   }



   /// <summary>Reads the literals of the <c>string[]</c> that a <c>foreach(string variable in array)</c> earlier in the same method walks; null when there is no such loop, no such array, or an item that is not a literal.</summary>
   private static List<string>? LoopedOver(string methodSoFar, string variable)
   {
      Match? loop = ForEachString().Matches(methodSoFar).LastOrDefault(match => string.Equals(match.Groups["variable"].Value, variable, StringComparison.Ordinal));
      if(loop is null)
      {
         return null;
      }
      Match? array = StringArray().Matches(methodSoFar).LastOrDefault(match => string.Equals(match.Groups["array"].Value, loop.Groups["array"].Value, StringComparison.Ordinal));
      if(array is null)
      {
         return null;
      }
      List<string> literals = [];
      foreach(string item in CSharpText.Items(methodSoFar, array.Index + array.Length - 1, declaration: false, out _))
      {
         if(!CSharpText.TryReadLiteral(item, out string literal))
         {
            return null;
         }
         literals.Add(literal);
      }
      return literals;
   }



   /// <summary><c>GetAutomationId(element), value</c>: an identifier read from an element and compared with a value.</summary>
   [GeneratedRegex("""\bGetAutomationId\s*\(\s*[A-Za-z_]\w*\s*\)\s*,\s*(?<value>"[^"\\]*"|[A-Za-z_]\w*)""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex ComparedIdentifier();



   /// <summary><c>foreach(string variable in array)</c>.</summary>
   [GeneratedRegex("""\bforeach\s*\(\s*string\s+(?<variable>[A-Za-z_]\w*)\s+in\s+(?<array>[A-Za-z_]\w*)\s*\)""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex ForEachString();



   /// <summary><c>string[] array = [</c>, ending at the opening bracket of its items.</summary>
   [GeneratedRegex("""\bstring\[\]\s+(?<array>[A-Za-z_]\w*)\s*=\s*\[""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex StringArray();



   /// <summary><c>FindName("Name")</c> or <c>Field&lt;T&gt;(window, "Name")</c>.</summary>
   [GeneratedRegex("""\b(FindName\s*\(|Field<[^<>]+>\s*\(\s*[A-Za-z_]\w*\s*,)\s*"(?<name>[^"\\]*)["]""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex NameLookup();
}
