/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>
/// The source files the project file links beside the tests as content: the companion's markup and code-behind, and
/// the two interactive suites. Only the files the build listed in <c>view-contract-sources.txt</c> are read, because a
/// copy stays in the output folder after its source is deleted or renamed.
/// </summary>
internal static class CompanionSources
{
   /// <summary>Every view's markup, named by its path below the companion's project folder.</summary>
   internal static IReadOnlyList<SourceFile> Markup() => Read("Companion/", ".xaml");



   /// <summary>Every C# file of the companion, named the same way.</summary>
   internal static IReadOnlyList<SourceFile> Code() => Read("Companion/", ".cs");



   /// <summary>The C# files of the UI tests and of the layout harness, named "UiTests/AppTests.cs" and the like.</summary>
   internal static IReadOnlyList<SourceFile> InteractiveSuites() => Read("InteractiveTests/", ".cs");



   /// <summary>The lines of one of the recorded lists that sit beside the contract tests.</summary>
   internal static string[] Recorded(string name) => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Views", name));



   /// <summary>The code-behind of one view: the files in its folder whose names start with the view's name and a dot, which is how a partial class is split here (MainWindow.xaml.cs, MainWindow.Jobs.cs).</summary>
   internal static IReadOnlyList<SourceFile> CodeBehindOf(SourceFile markup, IEnumerable<SourceFile> code)
   {
      string stem = markup.Name[..^"xaml".Length];
      return [.. code.Where(file => file.Name.StartsWith(stem, StringComparison.Ordinal))];
   }



   /// <summary>Reads the listed files below one linked folder that have the given extension, in name order.</summary>
   private static IReadOnlyList<SourceFile> Read(string folder, string extension)
      => [.. File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "view-contract-sources.txt"))
         .Select(line => line.Replace('\\', '/'))
         .Where(name => name.StartsWith(folder, StringComparison.Ordinal) && name.EndsWith(extension, StringComparison.Ordinal))
         .Order(StringComparer.Ordinal)
         .Select(name => new SourceFile(name[folder.Length..], File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name))))];
}
