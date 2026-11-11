/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>
/// One source file the view contract reads as text: a view's markup, its code-behind, or a file of an interactive
/// suite. The name is its path below the folder it was linked from, with forward slashes, so a failure names a place a
/// developer can open.
/// </summary>
internal sealed record SourceFile(string Name, string Text)
{
   /// <summary>The text with every C# comment blanked out, so a sentence about a control is never read as one. Markup is returned as it is.</summary>
   internal string Code { get; } = Name.EndsWith(".cs", StringComparison.Ordinal) ? CSharpText.WithoutComments(Text) : Text;



   /// <summary>The code with the inside of every string and character literal blanked as well, so a sentence in a string is never read as a keyword or a name. Positions are those of the text. Markup is returned as it is.</summary>
   internal string Bare { get; } = Name.EndsWith(".cs", StringComparison.Ordinal) ? CSharpText.WithoutLiterals(CSharpText.WithoutComments(Text)) : Text;



   /// <summary>The file and line of a position in the text, as "MainWindow.xaml.cs:168".</summary>
   internal string At(int index) => string.Create(CultureInfo.InvariantCulture, $"{Name}:{CSharpText.LineOf(Text, index)}");
}
