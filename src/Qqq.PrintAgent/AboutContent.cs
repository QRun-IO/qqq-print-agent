/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Qqq.PrintAgent;

/// <summary>
/// Composes the shared AppKit About body for this build: the generic build keeps every section; an edition may leave out the
/// Query mascot section and the License section through its descriptor while the header (name, tagline, version) and the
/// Open source components credits always remain. Deployment branding adds the support link and logo.
/// </summary>
internal static class AboutContent
{
   /// <summary>Applies the build's About choices to a freshly created dialog body.</summary>
   internal static void Compose(StackPanel body, Uri? supportUrl, BitmapImage? logo)
   {
      if(supportUrl is not null)
      {
         HyperlinkButton support = new() { Content = "Contact support", NavigateUri = supportUrl };
         Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(support, "AboutSupportLink");
         body.Children.Add(support);
      }
      if(logo is not null)
      {
         body.Children.Insert(0, new Image { Source = logo, Width = 128, Height = 64, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left });
      }
      if(AppIdentity.Edition?.ShowLicense == false)
      {
         StackPanel? license = body.Children.OfType<StackPanel>().FirstOrDefault(section => section.Children.FirstOrDefault() is TextBlock { Text: "License" });
         if(license is not null)
         {
            body.Children.Remove(license);
         }
      }
      if(AppIdentity.Edition?.ShowMascot != false)
      {
         QueryAboutSection.InsertAfterHeader(body);
      }
   }
}
