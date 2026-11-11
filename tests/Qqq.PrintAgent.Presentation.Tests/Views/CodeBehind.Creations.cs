/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.RegularExpressions;

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>
/// The objects a C# file of the companion creates, read from every <c>new</c> in it, and the types it declares.
/// <para>
/// The type of a creation is the one written after <c>new</c>, with or without a namespace or <c>global::</c> in
/// front. For a <c>new(</c> it is the type the same statement declares (<c>Kind name = new(</c>, a local, a field or
/// a property, nullable or not); or the type of the field, property or local the statement assigns, when the file
/// declares that name with one type (<c>name = new(</c>); or the return type of the method whose whole body it is
/// (<c>Kind Name(...) =&gt; new(</c>). A <c>new(</c> in any other place (a <c>return</c>, an argument, an item of a
/// collection) is returned without a type, and the contract fails on it: its type is written somewhere this reader
/// does not look, so it could be a control. An array, a generic type and a built-in type are never controls and are
/// left out.
/// </para>
/// </summary>
internal static partial class CodeBehind
{
   private const string TypeName = """(global::)?[A-Za-z_]\w*(\.[A-Za-z_]\w*)*(<[^;=(){}]*>)?(\[[,\s]*\])?\??""";
   private const int Nearby = 400;
   private static readonly HashSet<string> s_builtIn = new(StringComparer.Ordinal)
   {
      "string", "object", "dynamic", "bool", "char", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "nint", "nuint", "float", "double", "decimal",
   };



   /// <summary>
   /// One object created in code. <c>Kind</c> is its type's own name, null when the reader cannot name it.
   /// <c>Target</c> is the variable, field or property it is assigned to, null when it is created inside another
   /// expression. <c>Member</c> is true when the target is a field or a property of the class (declared with an access
   /// modifier in the file) and false for a local, including a local that is declared where it is created and has the
   /// name of a member. A member is one control wherever the file names it; a local is one within the braces around
   /// its creation (<c>CSharpText.BlockAround</c>).
   /// </summary>
   internal sealed record Creation(string Where, int Index, string? Kind, string? Target, bool Member);



   /// <summary>Every creation in the file that could be a control, in order.</summary>
   internal static IReadOnlyList<Creation> Creations(SourceFile file)
   {
      List<Creation> creations = [];
      IReadOnlyList<CSharpText.Method> methods = CSharpText.Methods(file.Code);
      var members = MemberDeclaration().Matches(file.Bare).Select(match => match.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
      foreach(Match match in New().Matches(file.Bare))
      {
         string before = file.Bare[Math.Max(0, match.Index - Nearby)..match.Index];
         string next = match.Groups["next"].Value;
         Creation? creation = null;
         if(match.Groups["type"].Success)
         {
            creation = next is "[" or "<" ? null : Typed(file.At(match.Index), match.Index, match.Groups["type"].Value, AssignedTo(before));
         }
         else if(string.Equals(next, "(", StringComparison.Ordinal) && !GenericConstraint().IsMatch(before))
         {
            creation = TargetTyped(file, methods, before, match.Index);
         }
         if(creation is not null)
         {
            Match local = LocalDeclaration().Match(before);
            bool declaredAsALocal = local.Success && !local.Groups["modifier"].Success && (IsAType(local.Groups["type"].Value) || local.Groups["type"].Value is "var");
            creations.Add(creation with { Member = creation.Target is not null && !declaredAsALocal && members.Contains(creation.Target.Split('[')[0]) });
         }
      }
      return creations;
   }



   /// <summary>The classes, records and structs the file declares, each with the type it derives from (or the first interface it implements), null when it names none.</summary>
   internal static IReadOnlyList<(string Name, string? Base)> Types(SourceFile file)
      => [.. TypeDeclaration().Matches(file.Bare).Select(match => (match.Groups["name"].Value, match.Groups["base"].Success ? match.Groups["base"].Value : null))];



   /// <summary>
   /// The menu a creation is added to with <c>menu.Items.Add(...)</c>, null when it is not added to one that way: either
   /// the creation is the argument itself, or the field or local it is assigned to is the argument of such a call in the
   /// same file.
   /// </summary>
   internal static string? AddedTo(SourceFile file, Creation creation)
   {
      Match direct = ItemsAddOpen().Match(file.Bare[Math.Max(0, creation.Index - Nearby)..creation.Index]);
      if(direct.Success)
      {
         return direct.Groups["menu"].Value;
      }
      if(creation.Target is null)
      {
         return null;
      }
      foreach(CSharpText.Call call in CSharpText.Calls(file.Code, "Add").Where(call => call.Arguments.Count == 1 && string.Equals(call.Arguments[0].Replace("this.", string.Empty, StringComparison.Ordinal), creation.Target, StringComparison.Ordinal)))
      {
         Match through = ItemsBeforeAdd().Match(file.Bare[Math.Max(0, call.Index - Nearby)..call.Index]);
         if(through.Success)
         {
            return through.Groups["menu"].Value;
         }
      }
      return null;
   }



   /// <summary>The menus the file gives to a tray icon: each name assigned to <c>ContextFlyout</c> in the initializer of a <c>new TaskbarIcon</c>.</summary>
   internal static IReadOnlySet<string> TrayMenus(SourceFile file)
   {
      HashSet<string> menus = new(StringComparer.Ordinal);
      foreach(Match icon in NewTaskbarIcon().Matches(file.Bare))
      {
         foreach(string item in CSharpText.Items(file.Code, icon.Index + icon.Length - 1, declaration: false, out _))
         {
            Match assigned = ContextFlyoutAssigned().Match(item);
            if(assigned.Success)
            {
               menus.Add(assigned.Groups["menu"].Value);
            }
         }
      }
      return menus;
   }



   /// <summary>
   /// What the file gives a handler for a press or a key (<c>target.Tapped += ...</c> and the like), each with the last
   /// name of what the handler is put on (<c>this</c> when the event is the class's own), the event and where it is.
   /// An operator acts on such an element, whatever its kind.
   /// </summary>
   internal static IReadOnlyList<(string Target, string Event, int Index)> InputTaken(SourceFile file)
      => [.. InputHandler().Matches(file.Bare).Select(match => (match.Groups["target"].Success ? match.Groups["target"].Value : "this", match.Groups["event"].Value, match.Index))];



   /// <summary>A creation whose type is written after <c>new</c> or found in a declaration; null when that type is never a control (generic, an array, built in, or not a type name at all).</summary>
   private static Creation? Typed(string where, int index, string type, string? target)
   {
      string name = type.TrimEnd('?');
      if(name.Contains('<', StringComparison.Ordinal) || name.Contains('[', StringComparison.Ordinal))
      {
         return null;
      }
      string kind = name[(name.LastIndexOfAny(['.', ':']) + 1)..].Trim();
      return char.IsUpper(kind[0]) ? new Creation(where, index, kind, target, Member: false) : null;
   }



   /// <summary>Names the type of a <c>new(</c> from the statement it is in, the declaration of what it is assigned to, or the method it is the whole body of. A creation without a kind is returned when none of these names it.</summary>
   private static Creation? TargetTyped(SourceFile file, IReadOnlyList<CSharpText.Method> methods, string before, int index)
   {
      string where = file.At(index);
      Match declared = DeclaredHere().Match(before);
      if(declared.Success && IsAType(declared.Groups["type"].Value))
      {
         return Typed(where, index, declared.Groups["type"].Value, declared.Groups["target"].Value);
      }
      string? target = AssignedTo(before);
      if(target is not null)
      {
         // Two declarations of the name with different types (a field and a local of another method) leave the type unnamed.
         string[] types = DeclaredTypes(file, target.Split('[')[0]);
         return types.Length == 1 ? Typed(where, index, types[0], target) : new Creation(where, index, Kind: null, target, Member: false);
      }
      CSharpText.Method? method = CSharpText.Enclosing(methods, index);
      if(method is not null && method.Close < index && string.Equals(file.Bare[(method.Close + 1)..index].Trim(), "=>", StringComparison.Ordinal))
      {
         Match returned = TypeAtTheEnd().Match(file.Bare[Math.Max(0, method.Index - Nearby)..method.Index]);
         if(returned.Success && IsAType(returned.Groups["type"].Value))
         {
            return Typed(where, index, returned.Groups["type"].Value, target: null);
         }
      }
      return new Creation(where, index, Kind: null, Target: null, Member: false);
   }



   /// <summary>The types the file declares a name with, as a field, a property, a local or a parameter, each once and without its question mark.</summary>
   private static string[] DeclaredTypes(SourceFile file, string name)
      => [.. Declaration().Matches(file.Bare).Where(match => string.Equals(match.Groups["name"].Value, name, StringComparison.Ordinal) && IsAType(match.Groups["type"].Value))
         .Select(match => match.Groups["type"].Value.TrimEnd('?')).Distinct(StringComparer.Ordinal)];



   /// <summary>The name a creation is assigned to when the statement assigns it to a plain name (or <c>this.name</c>, or an element of one); null when it is assigned to a member of something else or not assigned at all.</summary>
   private static string? AssignedTo(string before)
   {
      Match match = Assignment().Match(before);
      string path = match.Groups["path"].Value.Replace(" ", string.Empty, StringComparison.Ordinal);
      return match.Success && path is "" or "this." ? match.Groups["target"].Value : null;
   }



   /// <summary>True for what can be a declared type: a name that starts with a capital or with <c>global::</c>, or a built-in type. A keyword before a name (<c>return x = new()</c>) is not one.</summary>
   private static bool IsAType(string type) => char.IsUpper(type[0]) || type.StartsWith("global::", StringComparison.Ordinal) || s_builtIn.Contains(type.TrimEnd('?', ']', '['));



   /// <summary><c>new</c>, the type written after it when there is one, and the bracket that follows.</summary>
   [GeneratedRegex("""\bnew\b(\s+(?<type>(global::)?[A-Za-z_]\w*(\s*\.\s*[A-Za-z_]\w*)*))?\s*(?<next>[\[<({])?""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex New();



   /// <summary><c>Kind name =</c> or <c>Kind Name { get; } =</c> at the end of what precedes a <c>new(</c>.</summary>
   [GeneratedRegex("(?<type>" + TypeName + """)\s+(?<target>[A-Za-z_]\w*)\s*(\{[^{}]*\}\s*)?=\s*$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex DeclaredHere();



   /// <summary>A declaration of a name with its type: a field, a property, a local or a parameter.</summary>
   [GeneratedRegex("(?<type>" + TypeName + """)\s+(?<name>[A-Za-z_]\w*)\s*(;|=(?![=>])|\{|,|\))""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex Declaration();



   /// <summary>A type at the very end of a stretch, which is where a method's return type stands before its name.</summary>
   [GeneratedRegex("(?<type>" + TypeName + """)\s+$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex TypeAtTheEnd();



   /// <summary><c>name =</c>, <c>this.name =</c>, <c>name[0] =</c> or <c>Name { get; } =</c> at the end of what precedes a creation, with the path in front of the name when it is a member of something.</summary>
   [GeneratedRegex("""(?<![\w.])(?<path>([A-Za-z_]\w*\s*\.\s*)*)(?<target>[A-Za-z_]\w*(\[[^\[\]]*\])?)\s*(\{[^{}]*\}\s*)?=\s*$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex Assignment();



   /// <summary><c>Kind name =</c> or <c>var name =</c> at the end of what precedes a creation, with the modifier in front of the type when there is one: without a modifier it declares a local.</summary>
   [GeneratedRegex("""(?<modifier>\b(private|protected|internal|public|static|readonly|required|const)\s+)?(?<![\w.])(?<type>""" + TypeName + """)\s+[A-Za-z_]\w*\s*=\s*$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex LocalDeclaration();



   /// <summary>A field or a property: an access modifier, then a name directly before a semicolon, an initializer or accessors. A method has a bracket there and is not matched.</summary>
   [GeneratedRegex("""\b(private|protected|internal|public)\b[^;{}()=]*?[\s>\]?](?<name>[A-Za-z_]\w*)\s*(;|=(?!>)|\{)""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex MemberDeclaration();



   /// <summary>The <c>new()</c> of a generic constraint, which creates nothing.</summary>
   [GeneratedRegex("""\bwhere\s+[A-Za-z_]\w*\s*:[^;{}=()]*$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex GenericConstraint();



   /// <summary>A class, record or struct declaration with the first type after its colon, when it has one.</summary>
   [GeneratedRegex("""\b(class|struct|record(\s+(class|struct))?)\s+(?<name>[A-Za-z_]\w*)[^:{;]*(:\s*(global::)?([A-Za-z_]\w*\s*\.\s*)*(?<base>[A-Za-z_]\w*))?""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex TypeDeclaration();



   /// <summary>A handler added to an event of a press or a key, with the last name of the path in front of the event when there is one.</summary>
   [GeneratedRegex("""(?<![\w.])(([A-Za-z_]\w*\s*\.\s*)*(?<target>[A-Za-z_]\w*)(\[[^\[\]]*\])?\s*\.\s*)?(?<event>Click|Tapped|DoubleTapped|RightTapped|Holding|PointerPressed|PointerReleased|KeyDown|KeyUp|PreviewKeyDown|PreviewKeyUp)\s*\+=""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex InputHandler();



   /// <summary><c>menu.Items.Add(</c> at the end of what precedes a creation.</summary>
   [GeneratedRegex("""(?<menu>[A-Za-z_]\w*)\s*\.\s*Items\s*\.\s*Add\s*\(\s*$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex ItemsAddOpen();



   /// <summary><c>menu.Items.</c> at the end of what precedes an <c>Add</c>.</summary>
   [GeneratedRegex("""(?<menu>[A-Za-z_]\w*)\s*\.\s*Items\s*\.\s*$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex ItemsBeforeAdd();



   /// <summary><c>new TaskbarIcon {</c>, ending at the brace that opens its initializer.</summary>
   [GeneratedRegex("""\bnew\s+([A-Za-z_]\w*\s*\.\s*)*TaskbarIcon\s*(\(\s*\)\s*)?\{""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex NewTaskbarIcon();



   /// <summary><c>ContextFlyout = menu</c> as one item of an initializer.</summary>
   [GeneratedRegex("""^ContextFlyout\s*=\s*(?<menu>[A-Za-z_]\w*)$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex ContextFlyoutAssigned();
}
