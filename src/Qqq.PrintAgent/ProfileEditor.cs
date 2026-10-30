using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Qqq.PrintAgent;

/// <summary>Native bounded editor shared by computer and printer metadata dialogs.</summary>
internal sealed class ProfileEditor : StackPanel
{
   internal TextBox NameBox { get; } = new() { Header = "Name", MaxLength = 512 };
   internal TextBox LocationBox { get; } = new() { Header = "Location", MaxLength = 512 };
   internal TextBox DescriptionBox { get; } = new() { Header = "Description", MaxLength = 4096, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 180 };

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
}
