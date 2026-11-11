/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Qqq.PrintAgent.Presentation.Tests.Views;

/// <summary>Reads a view's markup as XML: every element with its kind, its name, and what it declares for automation. Nothing is compiled or rendered.</summary>
internal static class ViewMarkup
{
   private static readonly XNamespace s_language = "http://schemas.microsoft.com/winfx/2006/xaml";
   private static readonly string[] s_labels = ["Content", "Text", "Header", "Title", "PlaceholderText"];
   private static readonly string[] s_texts = ["Text", "Message", "Title"];
   private static readonly HashSet<string> s_input = new(StringComparer.Ordinal)
   {
      "Click", "Tapped", "DoubleTapped", "RightTapped", "Holding", "PointerPressed", "PointerReleased", "KeyDown", "KeyUp", "PreviewKeyDown", "PreviewKeyUp",
   };



   /// <summary>
   /// One element of a view. <c>Kind</c> is its tag (with "x:" in front for the markup language's own elements);
   /// <c>Name</c> is its <c>x:Name</c>; <c>AutomationId</c> and <c>LiveSetting</c> are the automation properties it
   /// declares, null when absent; <c>Bound</c> is true when its text follows a binding after the view is shown.
   /// <c>Control</c> says which element it is without a line number, so that it can be recorded: the tag and the
   /// <c>x:Name</c>, or, for an element without a name, the tag, its label and the nearest named element around it.
   /// <c>TakesInput</c> is true when the markup gives it a handler for a press or a key, or makes it a tab stop.
   /// </summary>
   internal sealed record Element(string Where, string Kind, string? Name, string? AutomationId, string? LiveSetting, bool Bound, string Control, bool TakesInput);



   /// <summary>Every element of the file, in document order. Property elements such as <c>Grid.Resources</c> set a property of their parent and are not elements of the view.</summary>
   internal static IReadOnlyList<Element> Elements(SourceFile file)
      => [.. XDocument.Parse(file.Text, LoadOptions.SetLineInfo).Descendants()
         .Where(element => !element.Name.LocalName.Contains('.', StringComparison.Ordinal))
         .Select(element => Read(file, element))];



   /// <summary>The class a view's controls belong to: the markup's own class, or the file's name when it declares none.</summary>
   internal static string OwnerOf(SourceFile file) => ClassOf(file) ?? file.Name;



   /// <summary>The class the markup belongs to, by its own name: the last part of the root element's <c>x:Class</c>, null when it declares none.</summary>
   internal static string? ClassOf(SourceFile file)
   {
      string? declared = (string?)XDocument.Parse(file.Text).Root?.Attribute(s_language + "Class");
      return declared?[(declared.LastIndexOf('.') + 1)..];
   }



   /// <summary>Reads one element. Where it is says the file, the line, the tag and the first of its content, text, header or title, so a failure can be found without counting columns.</summary>
   private static Element Read(SourceFile file, XElement element)
   {
      string kind = element.Name.Namespace == s_language ? "x:" + element.Name.LocalName : element.Name.LocalName;
      string? name = (string?)element.Attribute(s_language + "Name");
      string? text = s_labels.Select(attribute => (string?)element.Attribute(attribute)).FirstOrDefault(value => value is not null);
      string? label = text ?? name;
      string where = string.Create(CultureInfo.InvariantCulture, $"{file.Name}:{((IXmlLineInfo)element).LineNumber} <{kind}>{(label is null ? string.Empty : " \"" + label + "\"")}");
      string? defaultMode = element.AncestorsAndSelf().Select(around => (string?)around.Attribute(s_language + "DefaultBindMode")).FirstOrDefault(mode => mode is not null);
      return new Element(where, kind, name, (string?)element.Attribute("AutomationProperties.AutomationId"), (string?)element.Attribute("AutomationProperties.LiveSetting"),
         s_texts.Select(attribute => (string?)element.Attribute(attribute)).Any(value => FollowsABinding(value, defaultMode)), Control(element, kind, name, text), TakesInput(element));
   }



   /// <summary>Describes an element by what a developer would call it: "&lt;Button&gt; RefreshButton" for a named one; otherwise the tag, the label in quotes when it has one, and "in" the nearest element around it that has a name.</summary>
   private static string Control(XElement element, string kind, string? name, string? text)
   {
      if(name is not null)
      {
         return $"<{kind}> {name}";
      }
      string? around = element.Ancestors().Select(ancestor => (string?)ancestor.Attribute(s_language + "Name")).FirstOrDefault(found => found is not null);
      return $"<{kind}>{(text is null ? string.Empty : " \"" + text + "\"")}{(around is null ? string.Empty : " in " + around)}";
   }



   /// <summary>True when the element handles a press or a key, or is made a tab stop: an operator then acts on it, whatever its kind.</summary>
   private static bool TakesInput(XElement element)
      => element.Attributes().Any(attribute => s_input.Contains(attribute.Name.LocalName) || (string.Equals(attribute.Name.LocalName, "IsTabStop", StringComparison.Ordinal) && string.Equals(attribute.Value, "True", StringComparison.OrdinalIgnoreCase)));



   /// <summary>
   /// True for a value that changes after the view is shown: a <c>{Binding}</c>; an <c>{x:Bind}</c> that says it is
   /// one-way or two-way; or an <c>{x:Bind}</c> that says nothing where the element, or one around it, sets
   /// <c>x:DefaultBindMode</c> to one of those. Without either, an <c>{x:Bind}</c> is read once.
   /// </summary>
   private static bool FollowsABinding(string? value, string? defaultMode)
   {
      if(value is null || !value.StartsWith("{x:Bind", StringComparison.Ordinal))
      {
         return value is not null && value.StartsWith("{Binding", StringComparison.Ordinal);
      }
      return value.Contains("Mode=", StringComparison.Ordinal)
         ? value.Contains("Mode=OneWay", StringComparison.Ordinal) || value.Contains("Mode=TwoWay", StringComparison.Ordinal)
         : defaultMode is "OneWay" or "TwoWay";
   }
}
