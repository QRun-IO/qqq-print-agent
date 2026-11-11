/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.RegularExpressions;

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>
/// The names the companion's code gives to things while it runs. WinUI reports an element's <c>Name</c> as its
/// automation identifier when it has no other, so a name that is not written out is an identifier made at run time,
/// which only the generated forms may be, and those are set with <c>SetAutomationId</c>.
/// <para>
/// A name is given by an assignment to <c>Name</c>, plain or compound (<c>=</c>, <c>+=</c>, <c>??=</c>): in the
/// initializer of a <c>new</c>, as a statement of the class (with or without <c>this</c>), or on something else
/// (<c>row.Name = ...</c>). Each is returned with the kind of
/// what is named, as far as the file says it: the type created, the class the statement is in, or the one type the
/// file declares the receiver with; and without a kind where the file does not say. The contract fails on every one
/// that is not known to be something else than an element. Left alone are a value that is a plain string literal,
/// which is a fixed name as an <c>x:Name</c> in markup is; the declaration of a field or a local that is itself
/// called <c>Name</c>; an anonymous object; a <c>with</c> expression, which copies a record; and a member of an
/// <c>enum</c> that is called <c>Name</c> and given a value.
/// </para>
/// </summary>
internal static partial class CodeBehind
{
   /// <summary>One name given at run time: where; what is named, as a message says it ("this", "row", "a new Grid"); the kind of that, null where the file does not say; and the value as written.</summary>
   internal sealed record RunTimeName(string Where, string On, string? Kind, string Value);



   /// <summary>Every assignment to <c>Name</c> in the file whose value is not a plain string literal, with what it names.</summary>
   internal static IReadOnlyList<RunTimeName> NamesGivenAtRunTime(SourceFile file)
   {
      List<RunTimeName> names = [];
      foreach(Match match in NameAssigned().Matches(file.Bare))
      {
         string value = Flat(ValueGiven().Match(file.Code, match.Index + match.Length).Value);
         string before = file.Bare[..match.Index].TrimEnd();
         if(LiteralGiven().IsMatch(file.Code, match.Index + match.Length) || DeclaresAName(before))
         {
            continue;
         }
         (string On, string? Kind)? named = before.EndsWith('.') ? Receiver(file, before, match.Index) : Initialized(file, match.Index);
         if(named is not null)
         {
            names.Add(new RunTimeName(file.At(match.Index), named.Value.On, named.Value.Kind, value));
         }
      }
      return names;
   }



   /// <summary>True when what stands before <c>Name</c> is a type, so that the statement declares a field or a local called Name and assigns no property.</summary>
   private static bool DeclaresAName(string before)
   {
      if(before.Length == 0 || before.EndsWith("=>", StringComparison.Ordinal))
      {
         return false;
      }
      if(before[^1] is '?' or '>' or ']')
      {
         return true;
      }
      string word = char.IsLetterOrDigit(before[^1]) || before[^1] == '_' ? CSharpText.WordAt(before, before.Length - 1) : string.Empty;
      return word.Length > 0 && (IsAType(word) || string.Equals(word, "var", StringComparison.Ordinal));
   }



   /// <summary>What <c>receiver.Name</c> names: the class the line is in for <c>this</c>; for one plain name, the one type the file declares it with or creates it as; and nothing that can be named for a longer expression.</summary>
   private static (string On, string? Kind) Receiver(SourceFile file, string before, int index)
   {
      Match receiver = PlainReceiver().Match(before);
      if(!receiver.Success)
      {
         return ("an expression", null);
      }
      string name = receiver.Groups["name"].Value;
      if(string.Equals(name, "this", StringComparison.Ordinal))
      {
         return (name, OwnerOf(file, index));
      }
      string[] types = DeclaredTypes(file, name);
      if(types.Length == 0)
      {
         types = [.. Creations(file).Where(creation => creation.Kind is not null && string.Equals(creation.Target, name, StringComparison.Ordinal)).Select(creation => creation.Kind!).Distinct(StringComparer.Ordinal)];
      }
      return (name, types.Length == 1 ? types[0][(types[0].LastIndexOfAny(['.', ':']) + 1)..] : null);
   }



   /// <summary>
   /// What a bare <c>Name = ...</c> names, by the braces around it: the object a <c>new</c> creates when they are its
   /// initializer; the class the line is in when they are a block of statements; nothing that can be named when they
   /// initialize a member of something else (<c>Header = { Name = ... }</c>). Null for an anonymous object, for a
   /// <c>with</c> expression and for the members of an <c>enum</c>, which name no element.
   /// </summary>
   private static (string On, string? Kind)? Initialized(SourceFile file, int index)
   {
      int open = CSharpText.BlockAround(file.Bare, index).Open;
      string head = open < 0 ? string.Empty : file.Bare[..open].TrimEnd();
      if(WithOrAnEnumAtTheEnd().IsMatch(head))
      {
         return null;
      }
      if(head.EndsWith('='))
      {
         return ("the member an initializer fills", null);
      }
      string created = WithoutArguments(head);
      Match made = NewAtTheEnd().Match(created);
      if(!made.Success)
      {
         return ("this", OwnerOf(file, index));
      }
      if(made.Groups["type"].Success)
      {
         string type = made.Groups["type"].Value;
         string kind = type[(type.LastIndexOfAny(['.', ':']) + 1)..].Trim();
         return ("a new " + kind, kind);
      }
      // new() { ... } takes its type from what it is assigned to; new { ... } is an anonymous object.
      return created.Length == head.Length ? null : ("a new object", Creations(file).FirstOrDefault(creation => creation.Index == made.Index)?.Kind);
   }



   /// <summary>The text without the bracketed argument list it ends with, when it ends with one.</summary>
   private static string WithoutArguments(string head)
   {
      int depth = 0;
      for(int index = head.Length - 1; index >= 0 && head[^1] == ')'; index--)
      {
         depth += head[index] == ')' ? 1 : head[index] == '(' ? -1 : 0;
         if(depth == 0)
         {
            return head[..index].TrimEnd();
         }
      }
      return head;
   }



   /// <summary>An assignment to something called Name, also one that appends to it or fills it when it is null.</summary>
   [GeneratedRegex("""(?<!\w)Name\s*(\+|\?\?)?=(?![=>])""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex NameAssigned();



   /// <summary>What an assignment gives, up to the end of its statement or of its line.</summary>
   [GeneratedRegex("""\G[^;\r\n]*""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex ValueGiven();



   /// <summary>A plain string literal given as the whole value: what follows it ends the statement, the item or the initializer. The end of a line does not, because a sum may go on in the next.</summary>
   [GeneratedRegex("""\G\s*"[^"\\]*"\s*[;,})]""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex LiteralGiven();



   /// <summary>One plain name, or <c>this</c>, directly before a dot at the end, and not itself a member of something.</summary>
   [GeneratedRegex("""(?<![\w.)\]!?])(?<name>[A-Za-z_]\w*)\s*\.$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex PlainReceiver();



   /// <summary><c>new</c> at the end, with the type written after it when there is one.</summary>
   [GeneratedRegex("""\bnew\b\s*(?<type>(global::)?[A-Za-z_]\w*(\s*\.\s*[A-Za-z_]\w*)*(\s*<[^<>(){};=]*>)?)?$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex NewAtTheEnd();



   /// <summary>The word <c>with</c> at the end, or the head of an <c>enum</c>: its name, and its underlying type when it has one.</summary>
   [GeneratedRegex("""(?<!\w)(with|enum\s+[A-Za-z_]\w*(\s*:\s*[\w.]+)?)$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex WithOrAnEnumAtTheEnd();
}
