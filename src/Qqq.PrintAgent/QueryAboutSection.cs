/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Qqq.PrintAgent;

/// <summary>
/// Adds QQQ's mascot and public project links without replacing AppKit's identity or credits.
/// </summary>
internal static class QueryAboutSection
{
   /// <summary>Places Query after the brand header while retaining any preceding customer logo and all existing sections.</summary>
   internal static Grid InsertAfterHeader(StackPanel body)
   {
      Border header = body.Children.OfType<Border>().First();
      ConstrainHeader(header);
      Grid section = Create();
      body.Children.Insert(body.Children.IndexOf(header) + 1, section);
      return section;
   }



   /// <summary>Gives the original name a finite width without replacing AppKit's logo, text, fonts or gradient.</summary>
   private static void ConstrainHeader(Border header)
   {
      if(header.Child is not StackPanel { Children.Count: > 0 } content
         || content.Children[0] is not StackPanel { Orientation: Orientation.Horizontal, Children.Count: 2 } title
         || title.Children[0] is not Image logo || title.Children[1] is not TextBlock name)
      {
         return;
      }
      var row = new Grid { ColumnSpacing = title.Spacing };
      row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
      row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      title.Children.Clear();
      name.TextWrapping = TextWrapping.Wrap;
      Grid.SetColumn(name, 1);
      row.Children.Add(logo);
      row.Children.Add(name);
      content.Children[0] = row;
   }



   /// <summary>Keeps the mascot and wrapped links together in a compact native layout.</summary>
   private static Grid Create()
   {
      var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
      text.Children.Add(new TextBlock { Text = "Query", FontSize = 20, FontWeight = FontWeights.SemiBold });
      text.Children.Add(new TextBlock { Text = "QQQ and QRun.IO’s dog mascot.", TextWrapping = TextWrapping.Wrap });
      text.Children.Add(Link("Dogfooding at QRun.IO", "https://www.qrun.io/dogfooding", "AboutDogfoodingLink"));
      text.Children.Add(Link("Meet Query", "https://www.qrun.io/query", "AboutQueryLink"));
      var section = new Grid { ColumnSpacing = 12 };
      section.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
      section.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      Grid.SetColumn(text, 1);
      section.Children.Add(CreateCard());
      section.Children.Add(text);
      return section;
   }



   /// <summary>Keeps the original transparent black artwork visible in either application theme.</summary>
   private static Border CreateCard()
   {
      var image = new Image
      {
         Source = new BitmapImage(new Uri("ms-appx:///Assets/Query/query-mascot.png")),
         Stretch = Stretch.Uniform,
      };
      AutomationProperties.SetName(image, "Query, the QQQ and QRun.IO dog mascot");
      AutomationProperties.SetAutomationId(image, "AboutQueryMascot");

      return new Border
      {
         Width = 128,
         Height = 128,
         Padding = new Thickness(8),
         CornerRadius = new CornerRadius(8),
         HorizontalAlignment = HorizontalAlignment.Left,
         Background = new SolidColorBrush(Colors.White),
         Child = image,
      };
   }



   /// <summary>Uses native hyperlink navigation so links remain keyboard and screen-reader accessible.</summary>
   private static HyperlinkButton Link(string label, string address, string automationId)
   {
      var link = new HyperlinkButton
      {
         Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
         NavigateUri = new Uri(address),
         Padding = new Thickness(0),
         HorizontalAlignment = HorizontalAlignment.Stretch,
         HorizontalContentAlignment = HorizontalAlignment.Left,
      };
      AutomationProperties.SetAutomationId(link, automationId);
      return link;
   }
}
