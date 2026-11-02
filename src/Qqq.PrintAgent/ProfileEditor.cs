/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Qqq.PrintAgent;

/// <summary>Native bounded editor shared by computer and printer metadata dialogs.</summary>
internal sealed class ProfileEditor : StackPanel
{
   internal TextBox NameBox { get; } = new() { Header = "Name", MaxLength = 512 };
   internal TextBox LocationBox { get; } = new() { Header = "Location", MaxLength = 512 };
   internal TextBox DescriptionBox { get; } = new() { Header = "Description", MaxLength = 4096, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140, MaxHeight = 240, HorizontalAlignment = HorizontalAlignment.Stretch };

   internal ProfileEditor(string? name, string? description, string? location)
   {
      Spacing = 12;
      Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(NameBox, "MetadataName");
      Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(LocationBox, "MetadataLocation");
      Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(DescriptionBox, "MetadataDescription");
      NameBox.Text = name ?? string.Empty;
      DescriptionBox.Text = description ?? string.Empty;
      LocationBox.Text = location ?? string.Empty;
      Children.Add(NameBox);
      Children.Add(LocationBox);
      Children.Add(DescriptionBox);
   }

   internal void AddPrinterContext(string queue, string status)
   {
      Children.Add(new TextBlock { Text = "Windows printer", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
      Children.Add(new TextBlock { Text = queue, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
      Children.Add(new TextBlock { Text = $"Status: {status}\nThese details are displayed here and on the server. The Windows queue name and sharing approval remain unchanged.", TextWrapping = TextWrapping.Wrap });
   }
}
