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
   /// <summary>The name field, limited to 512 characters like the service's own check.</summary>
   internal TextBox NameBox { get; } = new() { Header = "Name", MaxLength = 512 };
   internal TextBox LocationBox { get; } = new() { Header = "Location", MaxLength = 512 };
   internal TextBox DescriptionBox { get; } = new() { Header = "Description", MaxLength = 4096, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140, MaxHeight = 240, HorizontalAlignment = HorizontalAlignment.Stretch };



   /// <summary>Builds the three fields, filled from the current values and tagged with automation identifiers for tests and screen readers; the limits match what the service accepts, so a value that fits here is not refused later.</summary>
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



   /// <summary>Adds a read-only block showing the Windows queue name and status, so the operator sees which printer is being described and that its queue name and sharing approval do not change.</summary>
   internal void AddPrinterContext(string queue, string status)
   {
      Children.Add(new TextBlock { Text = "Windows printer", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
      Children.Add(new TextBlock { Text = queue, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
      Children.Add(new TextBlock { Text = $"Status: {status}\nThese details are displayed here and on the server. The Windows queue name and sharing approval remain unchanged.", TextWrapping = TextWrapping.Wrap });
   }
}
