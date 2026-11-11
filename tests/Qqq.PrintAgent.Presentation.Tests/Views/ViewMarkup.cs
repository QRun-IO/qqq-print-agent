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



   /// <summary>
   /// One element of a view. <c>Kind</c> is its tag (with "x:" in front for the markup language's own elements);
   /// <c>Name</c> is its <c>x:Name</c>; <c>AutomationId</c> and <c>LiveSetting</c> are the automation properties it
   /// declares, null when absent; <c>Bound</c> is true when its text follows a binding after the view is shown.
   /// </summary>
   internal sealed record Element(string Where, string Kind, string? Name, string? AutomationId, string? LiveSetting, bool Bound);



   /// <summary>Every element of the file, in document order. Property elements such as <c>Grid.Resources</c> set a property of their parent and are not elements of the view.</summary>
   internal static IReadOnlyList<Element> Elements(SourceFile file)
      => [.. XDocument.Parse(file.Text, LoadOptions.SetLineInfo).Descendants()
         .Where(element => !element.Name.LocalName.Contains('.', StringComparison.Ordinal))
         .Select(element => Read(file, element))];



   /// <summary>Reads one element. Where it is says the file, the line, the tag and the first of its content, text, header or title, so a failure can be found without counting columns.</summary>
   private static Element Read(SourceFile file, XElement element)
   {
      string kind = element.Name.Namespace == s_language ? "x:" + element.Name.LocalName : element.Name.LocalName;
      string? name = (string?)element.Attribute(s_language + "Name");
      string? label = s_labels.Select(attribute => (string?)element.Attribute(attribute)).FirstOrDefault(value => value is not null) ?? name;
      string where = string.Create(CultureInfo.InvariantCulture, $"{file.Name}:{((IXmlLineInfo)element).LineNumber} <{kind}>{(label is null ? string.Empty : " \"" + label + "\"")}");
      return new Element(where, kind, name, (string?)element.Attribute("AutomationProperties.AutomationId"), (string?)element.Attribute("AutomationProperties.LiveSetting"),
         s_texts.Select(attribute => (string?)element.Attribute(attribute)).Any(FollowsABinding));
   }



   /// <summary>True for a value that changes after the view is shown: a <c>{Binding}</c>, or an <c>{x:Bind}</c> that is one-way or two-way (an <c>{x:Bind}</c> without a mode is read once).</summary>
   private static bool FollowsABinding(string? value)
      => value is not null && (value.StartsWith("{Binding", StringComparison.Ordinal)
         || (value.StartsWith("{x:Bind", StringComparison.Ordinal) && (value.Contains("Mode=OneWay", StringComparison.Ordinal) || value.Contains("Mode=TwoWay", StringComparison.Ordinal))));
}
