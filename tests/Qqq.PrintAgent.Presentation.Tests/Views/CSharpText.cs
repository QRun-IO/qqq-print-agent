/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text;
using System.Text.RegularExpressions;

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>
/// The little reading of C# that the view contract needs, done on text so that this project needs neither a compiler
/// package nor a reference to what it reads. It is not a parser: it blanks comments and the inside of literals, splits
/// a bracketed list at its commas, recognises a plain string literal, and finds where methods are declared and called.
/// Where it cannot follow the source it says so through what it returns, and the caller reports the place.
/// <para>
/// What that covers, and what it does not. The readers built on this report every form they know of and cannot
/// read: an identifier that is not written out, a <c>new(</c> whose type is written somewhere else, a mention of an
/// automation identifier outside the one call that sets it, a lookup in a form the suites' reader does not know. They
/// do not see what has no word of its own to look for: a control that is made without <c>new</c> in the companion
/// (by a factory of another assembly, or by reflection), and code inside the holes of an interpolated string, which
/// is blanked with the string. A declaration is
/// found by its shape (a name, a bracketed list of typed names, a body), so a method's extent is "up to the next
/// declaration"; a local function therefore ends the method it is in, which makes a rule about "the same method"
/// stricter, never looser.
/// </para>
/// </summary>
internal static partial class CSharpText
{
   private static readonly HashSet<string> s_notMethods = new(StringComparer.Ordinal)
   {
      "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "fixed", "when", "nameof", "typeof", "sizeof",
      "default", "return", "new", "this", "base", "await", "async", "throw", "checked", "unchecked", "is", "and", "or", "not",
   };



   /// <summary>A method (or constructor, or local function) declaration: its name, its parameter names in order, where its name starts, and where the bracket of its parameters closes.</summary>
   internal sealed record Method(string Name, IReadOnlyList<string> Parameters, int Index, int Close);



   /// <summary>A call: the name called, its arguments as written, where the name starts, and where the bracket of its arguments closes.</summary>
   internal sealed record Call(string Name, IReadOnlyList<string> Arguments, int Index, int Close);



   /// <summary>
   /// Returns the source with every comment replaced by spaces. Length and line breaks are kept, so a position in the
   /// result is the same position in the source, and string and character literals are left alone, so "https://" is
   /// not taken for a comment.
   /// </summary>
   internal static string WithoutComments(string source)
   {
      StringBuilder code = new(source);
      int index = 0;
      while(index < source.Length)
      {
         char next = index + 1 < source.Length ? source[index + 1] : '\0';
         if(source[index] == '/' && next == '/')
         {
            int end = source.IndexOf('\n', index);
            index = Blank(code, index, end < 0 ? source.Length : end);
         }
         else if(source[index] == '/' && next == '*')
         {
            int end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
            index = Blank(code, index, end < 0 ? source.Length : end + 2);
         }
         else
         {
            index = source[index] is '"' or '\'' ? EndOfLiteral(source, index) : index + 1;
         }
      }
      return code.ToString();
   }



   /// <summary>
   /// Returns code (already without comments) with the inside of every string and character literal replaced by spaces
   /// too, the quotes kept. Keywords and names are looked for in this, so that a sentence in a string ("start a new
   /// File") is never read as code. The holes of an interpolated string are blanked with it.
   /// </summary>
   internal static string WithoutLiterals(string code)
   {
      StringBuilder bare = new(code);
      int index = 0;
      while(index < code.Length)
      {
         if(code[index] is '"' or '\'')
         {
            int end = EndOfLiteral(code, index);
            Blank(bare, index + 1, Math.Max(index + 1, end - 1));
            index = end;
         }
         else
         {
            index++;
         }
      }
      return bare.ToString();
   }



   /// <summary>
   /// Reads the items of the bracketed list that opens at <paramref name="open"/> (a round or square bracket), split at
   /// the commas that are not inside a nested bracket or a literal, and gives the position of the closing bracket. In a
   /// declaration the angle brackets of a generic type nest too, so <c>Func&lt;A, B&gt; action</c> is one parameter; in
   /// a call they do not, because there they are also comparisons.
   /// </summary>
   internal static IReadOnlyList<string> Items(string code, int open, bool declaration, out int close)
   {
      List<string> items = [];
      int depth = 0;
      int start = open + 1;
      int index = start;
      while(index < code.Length)
      {
         char current = code[index];
         if(current is '"' or '\'')
         {
            index = EndOfLiteral(code, index);
            continue;
         }
         if(current is '(' or '[' or '{' || (declaration && current == '<'))
         {
            depth++;
         }
         else if(current is ')' or ']' or '}' || (declaration && current == '>' && code[index - 1] != '='))
         {
            if(depth == 0)
            {
               break;
            }
            depth--;
         }
         else if(current == ',' && depth == 0)
         {
            items.Add(code[start..index].Trim());
            start = index + 1;
         }
         index++;
      }
      close = Math.Min(index, code.Length);
      string last = code[start..close].Trim();
      if(last.Length > 0)
      {
         items.Add(last);
      }
      return items;
   }



   /// <summary>True for a plain string literal with nothing to unescape, which is the only way an identifier is written; anything else (a variable, a concatenation, an interpolation) is not a literal here.</summary>
   internal static bool TryReadLiteral(string expression, out string value)
   {
      Match match = PlainLiteral().Match(expression);
      value = match.Groups["value"].Value;
      return match.Success;
   }



   /// <summary>
   /// Finds the declarations in a file. A name before a bracket is a declaration when every item in the bracket is a
   /// type and a name, and a body follows: a brace, an arrow, a generic constraint, or a constructor's call to
   /// <c>this</c> or <c>base</c>. Calls, object creations and control statements are none of these.
   /// </summary>
   internal static IReadOnlyList<Method> Methods(string code)
   {
      List<Method> methods = [];
      foreach(Match match in NameBeforeBracket().Matches(code))
      {
         string name = match.Groups["name"].Value;
         if(s_notMethods.Contains(name) || FollowsNewOrADot(code, match.Index))
         {
            continue;
         }
         IReadOnlyList<string> parameters = Items(code, match.Index + match.Length - 1, declaration: true, out int close);
         Match[] names = [.. parameters.Select(parameter => ParameterName().Match(parameter))];
         if(names.All(parameter => parameter.Success) && StartsABody(code, close + 1))
         {
            methods.Add(new Method(name, [.. names.Select(parameter => parameter.Groups["name"].Value)], match.Index, close));
         }
      }
      return methods;
   }



   /// <summary>The declaration a position is in: the last one that starts before it, or null before the first.</summary>
   internal static Method? Enclosing(IReadOnlyList<Method> methods, int index) => methods.LastOrDefault(method => method.Index < index);



   /// <summary>Every place the name is followed by a bracket, with the bracket's items. A declaration of that name is among them; the caller tells it apart by its position.</summary>
   internal static IReadOnlyList<Call> Calls(string code, string name)
      => [.. NameBeforeBracket().Matches(code)
         .Where(match => string.Equals(match.Groups["name"].Value, name, StringComparison.Ordinal))
         .Select(match => new Call(name, Items(code, match.Index + match.Length - 1, declaration: false, out int close), match.Index, close))];



   /// <summary>Every call of the name that is not that name's own declaration.</summary>
   internal static IEnumerable<Call> CallsOnly(string code, string name, IReadOnlyList<Method> methods)
      => Calls(code, name).Where(call => !methods.Any(method => method.Index == call.Index));



   /// <summary>The stretch of the file a declaration covers: from its name to the name of the next declaration, or to the end.</summary>
   internal static string BodyOf(string code, IReadOnlyList<Method> methods, Method method)
   {
      Method? next = methods.FirstOrDefault(other => other.Index > method.Index);
      return code[method.Index..(next?.Index ?? code.Length)];
   }



   /// <summary>True when two positions are in the same declaration, or both before the first.</summary>
   internal static bool InTheSameMethod(IReadOnlyList<Method> methods, int first, int second) => Enclosing(methods, first)?.Index == Enclosing(methods, second)?.Index;



   /// <summary>The word a position is in: the letters, digits and underscores around it.</summary>
   internal static string WordAt(string text, int index)
   {
      int start = index;
      while(start > 0 && IsWordCharacter(text[start - 1]))
      {
         start--;
      }
      int end = index;
      while(end < text.Length && IsWordCharacter(text[end]))
      {
         end++;
      }
      return text[start..end];
   }



   /// <summary>The line, counted from one, that a position is on.</summary>
   internal static int LineOf(string text, int index) => text.AsSpan(0, Math.Min(index, text.Length)).Count('\n') + 1;



   /// <summary>Replaces a stretch with spaces, keeping its line breaks, and returns where it ended.</summary>
   private static int Blank(StringBuilder code, int from, int to)
   {
      for(int index = from; index < to; index++)
      {
         if(code[index] is not ('\n' or '\r'))
         {
            code[index] = ' ';
         }
      }
      return to;
   }



   /// <summary>
   /// Returns the position just after the literal that starts at a quote: a character, an ordinary string (which ends at
   /// its line's end if it is not closed), a verbatim string, or a raw string of three or more quotes.
   /// </summary>
   private static int EndOfLiteral(string source, int start)
   {
      if(source[start] == '\'')
      {
         return EndOfEscaped(source, start, '\'');
      }
      int quotes = 1;
      while(start + quotes < source.Length && source[start + quotes] == '"')
      {
         quotes++;
      }
      if(quotes >= 3)
      {
         int end = source.IndexOf(new string('"', quotes), start + quotes, StringComparison.Ordinal);
         return end < 0 ? source.Length : end + quotes;
      }
      if(quotes == 2)
      {
         return start + 2;
      }
      bool verbatim = start > 0 && (source[start - 1] == '@' || (source[start - 1] == '$' && start > 1 && source[start - 2] == '@'));
      if(!verbatim)
      {
         return EndOfEscaped(source, start, '"');
      }
      int index = start + 1;
      while(index < source.Length && (source[index] != '"' || (index + 1 < source.Length && source[index + 1] == '"')))
      {
         index += source[index] == '"' ? 2 : 1;
      }
      return Math.Min(index + 1, source.Length);
   }



   /// <summary>Returns the position after the closing quote of a literal in which a backslash escapes the next character.</summary>
   private static int EndOfEscaped(string source, int start, char quote)
   {
      int index = start + 1;
      while(index < source.Length && source[index] != quote && source[index] != '\n')
      {
         index += source[index] == '\\' ? 2 : 1;
      }
      return Math.Min(index + 1, source.Length);
   }



   /// <summary>True when the word before a position is <c>new</c> or the character before it is a dot: an object creation or a member call, neither of which declares anything.</summary>
   private static bool FollowsNewOrADot(string code, int index)
   {
      int end = index;
      while(end > 0 && char.IsWhiteSpace(code[end - 1]))
      {
         end--;
      }
      if(end == 0)
      {
         return false;
      }
      return code[end - 1] == '.' || (end >= 3 && code.AsSpan(end - 3, 3).SequenceEqual("new") && (end == 3 || !IsWordCharacter(code[end - 4])));
   }



   /// <summary>True when what follows a closing bracket begins a body.</summary>
   private static bool StartsABody(string code, int index)
   {
      ReadOnlySpan<char> rest = code.AsSpan(Math.Min(index, code.Length)).TrimStart();
      if(rest.StartsWith("{") || rest.StartsWith("=>") || rest.StartsWith("where "))
      {
         return true;
      }
      if(!rest.StartsWith(":"))
      {
         return false;
      }
      rest = rest[1..].TrimStart();
      return rest.StartsWith("this(") || rest.StartsWith("base(");
   }



   /// <summary>True for a letter, a digit or an underscore.</summary>
   private static bool IsWordCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';



   /// <summary>A name directly before an opening round bracket, with its type arguments between them when it has any.</summary>
   [GeneratedRegex("""(?<name>\b[A-Za-z_]\w*)\s*(<[^<>()]*>)?\s*\(""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex NameBeforeBracket();



   /// <summary>A parameter: something, white space, then its name, then perhaps a default value.</summary>
   [GeneratedRegex("""\S\s+(?<name>[A-Za-z_]\w*)\s*(=.*)?$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.Singleline, 1000)]
   private static partial Regex ParameterName();



   /// <summary>A string literal with no quote and no backslash inside.</summary>
   [GeneratedRegex("""^"(?<value>[^"\\]*)"$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex PlainLiteral();
}
