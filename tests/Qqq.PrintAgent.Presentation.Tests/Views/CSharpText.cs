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
/// automation identifier outside the one call that sets it, a lookup in a form the suites' reader does not know, an
/// identifier that a style's setter gives, and a name that code gives an element from a value (WinUI reports an
/// element's name as its identifier when it has no other). They do not see what has no word of its own to look for:
/// a control that is made without <c>new</c> in the companion (by a factory of another assembly, or by reflection);
/// an identifier or a name that a template or a style of another assembly gives; a name that code gives an element
/// as a plain literal, which is held as an <c>x:Name</c> is but is not compared with the recorded identifiers; a
/// string that a call returns straight into one of FlaUI's own lookups; and code inside the holes of an
/// interpolated string, which is blanked with the string. A binding's mode is read with or without spaces around
/// its equals sign.
/// </para>
/// <para>
/// Where a piece of code begins and ends is read from its brackets, not guessed. A declaration is found by its shape
/// (a name, a bracketed list of typed names, a body) and reaches to the brace that closes its body, or to the
/// semicolon that ends an expression body; a local function lies inside the method it is in. What stands between
/// declarations (a property's accessor, a field's initializer) is in no method: nothing there is a parameter of the
/// method before it. A local is matched within the innermost pair of braces around it, which is the block that
/// creates it, so the two branches of an <c>if</c> are two places and an accessor's body is its own. Both rest on
/// the braces of the file being balanced; code that a preprocessor directive leaves out is read as if it were there.
/// </para>
/// </summary>
internal static partial class CSharpText
{
   private static readonly HashSet<string> s_notMethods = new(StringComparer.Ordinal)
   {
      "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "fixed", "when", "nameof", "typeof", "sizeof",
      "default", "return", "new", "this", "base", "await", "async", "throw", "checked", "unchecked", "is", "and", "or", "not",
   };



   /// <summary>A method (or constructor, or local function) declaration: its name, its parameter names in order, where its name starts, where the bracket of its parameters closes, and where its body ends (the position after its last character).</summary>
   internal sealed record Method(string Name, IReadOnlyList<string> Parameters, int Index, int Close, int End);



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
            methods.Add(new Method(name, [.. names.Select(parameter => parameter.Groups["name"].Value)], match.Index, close, EndOfBody(code, close + 1)));
         }
      }
      return methods;
   }



   /// <summary>The declaration a position is in: the innermost one whose name stands before it and whose body ends after it, or null when it is in none (a field's initializer, a property's accessor).</summary>
   internal static Method? Enclosing(IReadOnlyList<Method> methods, int index) => methods.LastOrDefault(method => method.Index < index && index < method.End);



   /// <summary>Every place the name is followed by a bracket, with the bracket's items. A declaration of that name is among them; the caller tells it apart by its position.</summary>
   internal static IReadOnlyList<Call> Calls(string code, string name)
      => [.. NameBeforeBracket().Matches(code)
         .Where(match => string.Equals(match.Groups["name"].Value, name, StringComparison.Ordinal))
         .Select(match => new Call(name, Items(code, match.Index + match.Length - 1, declaration: false, out int close), match.Index, close))];



   /// <summary>Every call of the name that is not that name's own declaration.</summary>
   internal static IEnumerable<Call> CallsOnly(string code, string name, IReadOnlyList<Method> methods)
      => Calls(code, name).Where(call => !methods.Any(method => method.Index == call.Index));



   /// <summary>The stretch of the file a declaration covers: from its name to the end of its body.</summary>
   internal static string BodyOf(string code, Method method) => code[method.Index..method.End];



   /// <summary>
   /// The innermost pair of braces around a position: where the opening brace is and where the closing one is. Before
   /// the first brace it is the whole text (-1 and the length). Give it text without comments and without the inside
   /// of literals, so that a brace in a string is not one. For a local this is the block that creates it; for
   /// anything else it is the nearest braces, which may be an initializer's.
   /// </summary>
   internal static (int Open, int Close) BlockAround(string bare, int index)
   {
      int depth = 0;
      int open = Math.Min(index, bare.Length) - 1;
      while(open >= 0 && (bare[open] != '{' || depth > 0))
      {
         depth += bare[open] == '}' ? 1 : bare[open] == '{' ? -1 : 0;
         open--;
      }
      depth = 0;
      int close = Math.Max(index, 0);
      while(close < bare.Length && (bare[close] != '}' || depth > 0))
      {
         depth += bare[close] == '{' ? 1 : bare[close] == '}' ? -1 : 0;
         close++;
      }
      return (open, close);
   }



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



   /// <summary>
   /// Returns the position just after the body that begins at or after a position: after the brace that closes a block
   /// body, or after the semicolon that ends an expression body. What stands before the body (a generic constraint, a
   /// constructor's call to <c>this</c> or <c>base</c>) is passed over, and a closing bracket that was never opened
   /// ends the body before it.
   /// </summary>
   private static int EndOfBody(string code, int from)
   {
      int depth = 0;
      bool expression = false;
      int index = from;
      while(index < code.Length)
      {
         char current = code[index];
         if(current is '"' or '\'')
         {
            index = EndOfLiteral(code, index);
            continue;
         }
         if(current is '(' or '[' or '{')
         {
            depth++;
         }
         else if(current is ')' or ']' or '}')
         {
            depth--;
            if(depth < 0 || (depth == 0 && current == '}' && !expression))
            {
               return depth < 0 ? index : index + 1;
            }
         }
         else if(depth == 0 && current == ';')
         {
            return index + 1;
         }
         expression |= depth == 0 && current == '=' && index + 1 < code.Length && code[index + 1] == '>';
         index++;
      }
      return code.Length;
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
