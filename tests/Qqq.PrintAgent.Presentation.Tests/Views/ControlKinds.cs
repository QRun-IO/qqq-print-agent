/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>
/// Says what sort of thing a kind is, for an element of markup and for an object created in code alike: the lists the
/// contract keeps, and the companion's own classes, which need no list. A class of the companion is what the type it
/// derives from is, so one that derives from a button is interactive wherever it is used; a class with its own
/// <c>.xaml</c> file is a view, a container whose parts carry the identifiers; and a class that derives from nothing,
/// or only implements interfaces, is not an element at all. A kind that is none of these is unknown, and the contract
/// fails on it, in markup and in code.
/// </summary>
internal sealed class ControlKinds
{
   private const int Deepest = 16;
   private readonly IReadOnlySet<string> _interactive;
   private readonly IReadOnlySet<string> _texts;
   private readonly IReadOnlySet<string> _passive;
   private readonly IReadOnlySet<string> _notElements;
   private readonly Dictionary<string, string?> _declared = new(StringComparer.Ordinal);
   private readonly HashSet<string> _views;
   private readonly HashSet<string> _nameThemselves = new(StringComparer.Ordinal);



   /// <summary>Reads the companion's own classes from its code and its views from its markup, beside the four lists.</summary>
   internal ControlKinds(IReadOnlySet<string> interactive, IReadOnlySet<string> texts, IReadOnlySet<string> passive, IReadOnlySet<string> notElements, IEnumerable<SourceFile> markup, IEnumerable<SourceFile> code)
   {
      _interactive = interactive;
      _texts = texts;
      _passive = passive;
      _notElements = notElements;
      _views = markup.Select(ViewMarkup.ClassOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
      foreach(SourceFile file in code)
      {
         IReadOnlyList<(string Name, string? Base)> types = CodeBehind.Types(file);
         foreach((string name, string? parent) in types)
         {
            // A partial class is declared in several files and names its base in one of them.
            _declared[name] = parent ?? _declared.GetValueOrDefault(name);
         }
         _nameThemselves.UnionWith(CodeBehind.Named(file).Where(named => string.Equals(named.Target, "this", StringComparison.Ordinal)).Select(named => CodeBehind.OwnerOf(file, named.Index)));
      }
   }



   /// <summary>What a kind is.</summary>
   internal enum Sort
   {
      /// <summary>An operator acts on it: it needs an identifier.</summary>
      Interactive,

      /// <summary>It shows words the window can change.</summary>
      Text,

      /// <summary>Another element of a view: layout, a picture, a container.</summary>
      Passive,

      /// <summary>Not part of a view at all: a value, a command, a service.</summary>
      NotAnElement,

      /// <summary>In none of the lists and not a class of the companion.</summary>
      Unknown,
   }



   /// <summary>Classifies a kind by the lists first and then, for a class of the companion, by what it derives from.</summary>
   internal Sort Of(string kind) => Of(kind, depth: 0);



   /// <summary>True for a class of the companion that sets an identifier on <c>this</c> in its own body: every instance then carries that identifier, wherever it is created. Another class in the same file does not.</summary>
   internal bool NamesItself(string kind) => _nameThemselves.Contains(kind);



   /// <summary>The classification, with how many classes of the companion have been followed so far, so that two classes that name each other end.</summary>
   private Sort Of(string kind, int depth)
   {
      if(_interactive.Contains(kind))
      {
         return Sort.Interactive;
      }
      if(_texts.Contains(kind) || _passive.Contains(kind) || _notElements.Contains(kind))
      {
         return _texts.Contains(kind) ? Sort.Text : _passive.Contains(kind) ? Sort.Passive : Sort.NotAnElement;
      }
      if(!_declared.TryGetValue(kind, out string? parent))
      {
         return _views.Contains(kind) ? Sort.Passive : Sort.Unknown;
      }
      Sort inherited = parent is null || IsAnInterface(parent) || depth >= Deepest ? Sort.NotAnElement : Of(parent, depth + 1);
      return inherited != Sort.Interactive && _views.Contains(kind) ? Sort.Passive : inherited;
   }



   /// <summary>True for a name written as interfaces are: a capital I and another capital.</summary>
   private static bool IsAnInterface(string name) => name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]);
}
