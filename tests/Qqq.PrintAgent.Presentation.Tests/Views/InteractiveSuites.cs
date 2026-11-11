/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text;
using System.Text.RegularExpressions;

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>
/// What the two interactive suites look up, read from their source. The UI tests (FlaUI, against the built companion)
/// and the layout harness (the real views over a synthetic service) are not in the gate, so nothing else fails when a
/// view stops carrying what they look for.
/// <para>
/// An automation identifier is the argument, at the position given below, of one of the suites' own lookups, or the
/// literal an identifier read with <c>GetAutomationId</c> is compared with by <c>string.Equals</c>. The argument is a
/// string literal; or the variable of a <c>foreach</c> over a <c>string[]</c> of literals; or a parameter of the
/// helper the call is in, which must then be a lookup itself at that position, so that its callers are read in turn.
/// </para>
/// <para>
/// FlaUI's own <c>FindFirstDescendant</c> and <c>FindFirstChild</c> find by automation identifier when their one
/// argument is a string. FlaUI.Core 5.0.0 documents those two overloads so, and no other <c>Find</c> method of it
/// takes an identifier as a string. <c>FindAllDescendants</c> and <c>FindAllChildren</c> have no such overload in
/// that version and are read in the same way, so that a later FlaUI, or an extension method of that name, is not
/// skipped. Whether the argument is a string is read from how it is written. A string literal is the identifier.
/// Anything else that starts with a quote, and a name the method declares as a <c>string</c>, is an identifier too
/// and is read as any other lookup's argument is (a loop's variable, a forwarding helper's parameter), or reported.
/// A lambda, a call (<c>cf.ByName("...")</c>, a <c>new</c>) and a name the method declares with another type are
/// conditions, and are left to the lookups inside them. What is none of these (a name the method does not declare,
/// or declares with <c>var</c>; a member of something; an element of an array; a sum; a choice; a cast) cannot be
/// told apart and is reported. A sum, a choice and an element of an array are reported with a call in them too: a
/// <c>+</c>, a <c>?</c> or a <c>[</c> outside the argument's brackets makes it no condition, and only a
/// <c>=&gt;</c> outside them makes it a lambda. A call that is known to give a string is reported as well:
/// <c>nameof</c>, a method of <c>string</c>, and what ends in <c>ToString</c>, with or without arguments.
/// </para>
/// <para>
/// What is still unseen follows from that. An argument with a round bracket that is inside no other bracket (a
/// call, a <c>new</c>), which starts with neither a quote nor a bracket, has none of those three marks outside its
/// brackets and is none of the calls known to give a string, is taken for a condition whatever it gives. So a
/// string written that way goes unread (<c>Position(2)</c>, <c>row.Id()</c>, <c>Rows.First().Id</c>,
/// <c>await Pick()</c>, <c>new string('x', 2)</c>), and so does a name the method declares with a type that is a
/// string under another name (an alias, or a type that converts to one).
/// </para>
/// <para>
/// Every other place where a suite's source says <c>AutomationId</c> is reported: an XPath, a comparison of an
/// element's <c>AutomationId</c> property, an identifier compared with <c>==</c>, a lookup handed over as a delegate.
/// The only exception is a parameter of the method the word is in. So an identifier looked up in a form this reader
/// does not know fails a test and is not skipped. An element name is the literal, or the <c>nameof</c>, given to
/// <c>FindName</c> or to the harness's <c>Field&lt;T&gt;(window, name)</c>; anything else there is reported too.
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

   /// <summary>FlaUI's own lookups that take an automation identifier when their one argument is a string.</summary>
   private static readonly string[] s_byIdentifierWhenGivenAString = ["FindFirstDescendant", "FindFirstChild", "FindAllDescendants", "FindAllChildren"];

   private static readonly HashSet<string> s_notTypes = new(StringComparer.Ordinal)
   {
      "return", "await", "throw", "yield", "new", "out", "in", "ref", "is", "as", "case", "else", "not", "and", "or", "when", "params", "scoped", "this",
   };

   private static readonly Dictionary<string, int> s_nameLookups = new(StringComparer.Ordinal)
   {
      ["FindName"] = 0,
      ["Field"] = 1,
   };



   /// <summary>Every automation identifier the suites look up, in order. A lookup whose identifier cannot be read, and a mention of an automation identifier that is not one of the lookups read here, is added to <paramref name="unreadable"/> with its place.</summary>
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
               string lookup = CSharpText.WordAt(file.Code, index);
               string flaUi = s_byIdentifierWhenGivenAString.Contains(lookup, StringComparer.Ordinal) ? $" FlaUI's {lookup} finds by automation identifier when it is given a string. If this is a condition and no string, write it as a lambda (cf => ...), or declare it in this method with its type, so that the contract can tell." : string.Empty;
               unreadable.Add($"{file.At(index)}: the identifier looked up here ({argument}) cannot be read. Write it as a string literal, or add the helper that forwards it to the lookups in InteractiveSuites.{flaUi}");
               continue;
            }
            identifiers.UnionWith(read);
         }
         foreach(int mention in Unrecognised(file, methods))
         {
            unreadable.Add($"{file.At(mention)}: {CSharpText.WordAt(file.Code, mention)} is used here outside a lookup the contract reads, so an identifier looked up this way would go unread. Find the element with one of the suites' own lookups ({string.Join(", ", s_lookups.Keys)}), or compare with string.Equals(AutomationProperties.GetAutomationId(element), \"Identifier\", StringComparison.Ordinal). An XPath, a comparison with ==, and an element's AutomationId property are not read; if another form is needed, teach it to InteractiveSuites in the same change.");
         }
      }
      return identifiers;
   }



   /// <summary>
   /// Every element name the suites look up with <c>FindName(name)</c> or read as a field of the window with
   /// <c>Field&lt;T&gt;(window, name)</c>, in order, the name being a string literal or a <c>nameof</c>. A lookup whose
   /// name is neither is added to <paramref name="unreadable"/> with its place.
   /// </summary>
   internal static SortedSet<string> ElementNamesLookedUp(IEnumerable<SourceFile> files, ICollection<string> unreadable)
   {
      SortedSet<string> names = new(StringComparer.Ordinal);
      foreach(SourceFile file in files)
      {
         IReadOnlyList<CSharpText.Method> methods = CSharpText.Methods(file.Code);
         foreach((string lookup, int position) in s_nameLookups)
         {
            foreach(CSharpText.Call call in CSharpText.CallsOnly(file.Code, lookup, methods))
            {
               string argument = call.Arguments.Count > position ? call.Arguments[position] : string.Empty;
               Match named = NameOf().Match(argument);
               if(CSharpText.TryReadLiteral(argument, out string literal) || named.Success)
               {
                  names.Add(named.Success ? named.Groups["name"].Value : literal);
                  continue;
               }
               unreadable.Add($"{file.At(call.Index)}: the element name looked up here ({argument}) cannot be read. Write it as a string literal or as nameof(MainWindow.TheElement).");
            }
         }
      }
      return names;
   }



   /// <summary>The places an identifier is looked up in one file, each with the argument that carries it. A helper's own declaration is not a lookup, and neither is a call of FlaUI's own lookups with a condition.</summary>
   private static IEnumerable<(int Index, string Argument)> Lookups(SourceFile file, IReadOnlyList<CSharpText.Method> methods)
   {
      foreach((string name, int position) in s_lookups)
      {
         foreach(CSharpText.Call call in CSharpText.CallsOnly(file.Code, name, methods))
         {
            yield return (call.Index, call.Arguments.Count > position ? call.Arguments[position] : string.Empty);
         }
      }
      foreach(CSharpText.Call call in s_byIdentifierWhenGivenAString.SelectMany(name => CSharpText.CallsOnly(file.Code, name, methods)))
      {
         if(call.Arguments.Count == 1 && !IsACondition(file, methods, call))
         {
            yield return (call.Index, call.Arguments[0]);
         }
      }
      foreach(Match match in ComparedIdentifier().Matches(file.Code))
      {
         yield return (match.Index, match.Groups["value"].Value);
      }
   }



   /// <summary>
   /// The places where the file says <c>AutomationId</c> and it is none of: the name of a lookup read here (called or
   /// declared), a <c>GetAutomationId</c> whose result is compared in the form read here, or a parameter of the method
   /// the word is in. Literals are searched too, because an XPath is one.
   /// </summary>
   private static IEnumerable<int> Unrecognised(SourceFile file, IReadOnlyList<CSharpText.Method> methods)
   {
      List<(int Start, int Length)> read = [.. s_lookups.Keys.SelectMany(name => CSharpText.Calls(file.Code, name)).Select(call => (call.Index, call.Name.Length))];
      read.AddRange(ComparedIdentifier().Matches(file.Code).Select(match => (match.Index, "GetAutomationId".Length)));
      foreach(Match mention in AutomationId().Matches(file.Code))
      {
         bool parameter = CSharpText.Enclosing(methods, mention.Index)?.Parameters.Contains(CSharpText.WordAt(file.Code, mention.Index), StringComparer.Ordinal) == true;
         if(!parameter && !read.Any(lookup => mention.Index >= lookup.Start && mention.Index < lookup.Start + lookup.Length))
         {
            yield return mention.Index;
         }
      }
   }



   /// <summary>
   /// True when the one argument of a FlaUI lookup is written as a condition: a lambda, a call, or a name the method
   /// it is in declares with a type that is not <c>string</c>. False for everything else, which is then read as an
   /// identifier or reported: what starts as a string does, and what cannot be told from one (a bracketed expression
   /// or a cast among them, and a sum, a choice or an indexing even when there is a call in it). What makes a lambda,
   /// a call or one of those three is looked for outside the argument's brackets, so that a sum inside a condition's
   /// own arguments does not count and a lambda inside a sum's does not either.
   /// </summary>
   private static bool IsACondition(SourceFile file, IReadOnlyList<CSharpText.Method> methods, CSharpText.Call call)
   {
      string argument = call.Arguments[0];
      string outside = OutsideBrackets(argument);
      bool lambda = outside.Contains("=>", StringComparison.Ordinal);
      if(StartsAString().IsMatch(argument) || argument.StartsWith('(') || GivesAString().IsMatch(outside))
      {
         return argument.StartsWith('(') && lambda;
      }
      if(lambda || (outside.Contains('(', StringComparison.Ordinal) && outside.IndexOfAny(['+', '?', '[']) < 0))
      {
         return true;
      }
      CSharpText.Method? method = CSharpText.Enclosing(methods, call.Index);
      string[] types = method is null ? [] : [.. DeclaredName().Matches(file.Code[method.Index..call.Index])
         .Where(match => string.Equals(match.Groups["name"].Value, argument, StringComparison.Ordinal) && !s_notTypes.Contains(match.Groups["type"].Value))
         .Select(match => match.Groups["type"].Value.TrimEnd('?'))];
      return types.Length > 0 && !types.Any(type => type is "string" or "String" or "System.String" or "var");
   }



   /// <summary>The argument without what stands inside its brackets (round, square or curly) and inside its literals. The brackets and the quotes themselves are kept, so a call still shows as one.</summary>
   private static string OutsideBrackets(string argument)
   {
      StringBuilder outside = new();
      int depth = 0;
      foreach(char current in CSharpText.WithoutLiterals(argument))
      {
         depth -= current is ')' or ']' or '}' ? 1 : 0;
         if(depth <= 0)
         {
            outside.Append(current);
         }
         depth += current is '(' or '[' or '{' ? 1 : 0;
      }
      return outside.ToString();
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



   /// <summary>The start of a string: a quote, with the marks of an interpolated or a verbatim string in front when it has them.</summary>
   [GeneratedRegex("^[$@]{0,2}\"", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex StartsAString();



   /// <summary>A call that gives a string whatever it is given: <c>nameof(...)</c>, a method of <c>string</c>, and anything that ends in <c>ToString()</c>. It is matched against the argument without what stands inside its brackets, so <c>ToString</c> may have arguments.</summary>
   [GeneratedRegex("""^nameof\s*\(|^(string|String)\s*\.|\.\s*ToString\s*\(\s*\)$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex GivesAString();



   /// <summary>A name with the type in front of it, where a parameter, a local or a loop's variable is declared.</summary>
   [GeneratedRegex("""(?<![\w.])(?<type>[A-Za-z_][\w.]*(<[^;=(){}]*>)?(\[[,\s]*\])?\??)\s+(?<name>[A-Za-z_]\w*)\b\s*(?=[,;=)]|in\b)""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex DeclaredName();



   /// <summary>The words an automation identifier is named by, wherever they stand.</summary>
   [GeneratedRegex("""AutomationId""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex AutomationId();



   /// <summary><c>foreach(string variable in array)</c>.</summary>
   [GeneratedRegex("""\bforeach\s*\(\s*string\s+(?<variable>[A-Za-z_]\w*)\s+in\s+(?<array>[A-Za-z_]\w*)\s*\)""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex ForEachString();



   /// <summary><c>string[] array = [</c>, ending at the opening bracket of its items.</summary>
   [GeneratedRegex("""\bstring\[\]\s+(?<array>[A-Za-z_]\w*)\s*=\s*\[""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex StringArray();



   /// <summary><c>nameof(Name)</c> or <c>nameof(Type.Name)</c>, with the last name.</summary>
   [GeneratedRegex("""^nameof\s*\(\s*([A-Za-z_]\w*\s*\.\s*)*(?<name>[A-Za-z_]\w*)\s*\)$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex NameOf();
}
