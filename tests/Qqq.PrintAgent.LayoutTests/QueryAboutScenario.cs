/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using KofTwentyTwo.AppKit;
using KofTwentyTwo.AppKit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Qqq.PrintAgent;

/// <summary>Exercises the bundled image and scrollable shared dialog on a real, isolated WinUI surface.</summary>
internal static class QueryAboutScenario
{
   /// <summary>Checks both themes with long customer identity text without opening a browser or service connection.</summary>
   internal static async Task RunAsync(FrameworkElement root)
   {
      AppInfo identity = AppIdentity.DefaultInfo with
      {
         DisplayName = "North Distribution Centre — Customer Warehouse Printing Operations",
         Description = "A long customer description remains in the shared About dialog together with the version, license and existing component credits.",
      };
      foreach(ElementTheme theme in new[] { ElementTheme.Light, ElementTheme.Dark })
      {
         var dialog = new AboutDialog(identity) { XamlRoot = root.XamlRoot, RequestedTheme = theme };
         var scroll = (ScrollViewer)dialog.Content;
         var body = (StackPanel)scroll.Content;
         UIElement[] originalContent = body.Children.ToArray();
         Grid section = QueryAboutSection.InsertAfterHeader(body);
         Border card = section.Children.OfType<Border>().Single();
         var image = (Image)card.Child;
         bool imageOpened = false;
         bool imageFailed = false;
         image.ImageOpened += (_, _) => imageOpened = true;
         image.ImageFailed += (_, _) => imageFailed = true;
         Task<ContentDialogResult?> showing = DialogGuard.ShowAsync(dialog);
         try
         {
            await WaitAsync(() => imageOpened || imageFailed);
            VerifyImage(image, imageFailed);
            if(!body.Children.Where(element => !ReferenceEquals(element, section)).SequenceEqual(originalContent))
            {
               throw new InvalidOperationException("The existing About content changed.");
            }
            await VerifyOpeningAsync(dialog, scroll, section, identity.DisplayName, theme);
            HyperlinkButton query = Descendants(section).OfType<HyperlinkButton>().Last();
            Rect bounds = query.TransformToVisual(scroll).TransformBounds(new Rect(0, 0, query.ActualWidth, query.ActualHeight));
            if(bounds.Top < -1 || bounds.Bottom > scroll.ViewportHeight + 1
               || bounds.Left < -1 || bounds.Right > scroll.ViewportWidth + 1 || !query.Focus(FocusState.Keyboard)
               || !string.Equals(query.NavigateUri.AbsoluteUri, "https://www.qrun.io/query", StringComparison.Ordinal))
            {
               throw new InvalidOperationException("The Query link was not keyboard reachable within the native scroll viewport.");
            }
            HyperlinkButton dogfooding = Descendants(section).OfType<HyperlinkButton>().First();
            if(!string.Equals(dogfooding.NavigateUri.AbsoluteUri, "https://www.qrun.io/dogfooding", StringComparison.Ordinal)
               || string.IsNullOrWhiteSpace(AutomationProperties.GetName(image)))
            {
               throw new InvalidOperationException("The public links or mascot accessibility description were unavailable.");
            }
            await CaptureAsync(card, $"about-query-{theme}.png", requireContrast: true);
            scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
            await WaitAsync(() => scroll.VerticalOffset > 0 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1);
            if(!VisibleIn((FrameworkElement)body.Children.Last(), scroll))
            {
               throw new InvalidOperationException("The original AppKit credits were not available after scrolling.");
            }
            CloseThroughButton(dialog);
            await showing;
         }
         finally
         {
            dialog.Hide();
            await showing;
         }
      }
   }



   /// <summary>Requires a decoded bundled asset rather than a correctly named but missing resource.</summary>
   private static void VerifyImage(Image image, bool failed)
   {
      if(failed || image.ActualWidth <= 0 || image.ActualHeight <= 0
         || image.Source is not BitmapImage { PixelWidth: 1326, PixelHeight: 1394 })
      {
         throw new InvalidOperationException("The native resource loader did not decode the official mascot dimensions.");
      }
   }



   /// <summary>Captures the initial native layout before scrolling can hide a clipped name or an offscreen link.</summary>
   private static async Task VerifyOpeningAsync(ContentDialog dialog, ScrollViewer scroll, FrameworkElement section, string displayName, ElementTheme theme)
   {
      dialog.UpdateLayout();
      TextBlock name = Descendants(dialog).OfType<TextBlock>().Single(text => string.Equals(text.Text, displayName, StringComparison.Ordinal));
      FrameworkElement[] required = Descendants(section).OfType<FrameworkElement>()
         .Where(element => element is Image or HyperlinkButton).ToArray();
      bool queryVisible = required.Length == 3 && required.All(element => VisibleIn(element, scroll));
      bool nameVisible = VisibleIn(name, scroll) && !name.IsTextTrimmed && name.DesiredSize.Width <= name.ActualWidth + 1;
      File.WriteAllText(Path.Combine(Program.EvidenceDirectory, $"about-initial-{theme}.json"), JsonSerializer.Serialize(new
      {
         queryVisible,
         nameVisible,
         nameWidth = name.ActualWidth,
         nameHeight = name.ActualHeight,
         scroll.ViewportWidth,
         scroll.ViewportHeight,
         scroll.VerticalOffset,
      }));
      await CaptureAsync(dialog, $"about-dialog-{theme}.png", requireContrast: false);
      if(!queryVisible || !nameVisible || scroll.VerticalOffset != 0)
      {
         throw new InvalidOperationException("The initial About viewport clipped its customer name, mascot or public links.");
      }
   }



   /// <summary>Checks the actual arranged rectangle, rather than trusting wrapping or visibility properties.</summary>
   private static bool VisibleIn(FrameworkElement element, ScrollViewer scroll)
   {
      Rect bounds = element.TransformToVisual(scroll).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
      return bounds.Top >= -1 && bounds.Left >= -1 && bounds.Bottom <= scroll.ViewportHeight + 1
         && bounds.Right <= scroll.ViewportWidth + 1 && bounds.Width > 0 && bounds.Height > 0;
   }



   /// <summary>Checks optional card contrast and retains only synthetic native-dialog pixels for visual review.</summary>
   private static async Task CaptureAsync(FrameworkElement target, string fileName, bool requireContrast)
   {
      var rendering = new RenderTargetBitmap();
      await rendering.RenderAsync(target);
      IBuffer buffer = await rendering.GetPixelsAsync();
      byte[] pixels = new byte[checked((int)buffer.Length)];
      using(var reader = DataReader.FromBuffer(buffer))
      {
         reader.ReadBytes(pixels);
      }
      int white = 0;
      int black = 0;
      for(int index = 0; index < pixels.Length; index += 4)
      {
         if(pixels[index + 3] == byte.MaxValue)
         {
            if(pixels[index] > 245 && pixels[index + 1] > 245 && pixels[index + 2] > 245)
            {
               white++;
            }
            if(pixels[index] < 32 && pixels[index + 1] < 32 && pixels[index + 2] < 32)
            {
               black++;
            }
         }
      }
      if(requireContrast && (white < pixels.Length / 8 || black < 20))
      {
         throw new InvalidOperationException("The rendered Query card did not retain visible black artwork on white.");
      }
      StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(Program.EvidenceDirectory);
      StorageFile file = await folder.CreateFileAsync(fileName, CreationCollisionOption.FailIfExists);
      using IRandomAccessStream output = await file.OpenAsync(FileAccessMode.ReadWrite);
      BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
      encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
         checked((uint)rendering.PixelWidth), checked((uint)rendering.PixelHeight), 96, 96, pixels);
      await encoder.FlushAsync();
   }



   /// <summary>Uses the framework's actual close action after scrolling instead of assuming its template stays usable.</summary>
   private static void CloseThroughButton(ContentDialog dialog)
   {
      Button close = Descendants(dialog).OfType<Button>().Single(button => string.Equals(button.Name, "CloseButton", StringComparison.Ordinal));
      AutomationPeer peer = FrameworkElementAutomationPeer.CreatePeerForElement(close)
         ?? throw new InvalidOperationException("The shared About close button has no native automation peer.");
      if(!close.Focus(FocusState.Keyboard) || peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
      {
         throw new InvalidOperationException("The shared About close button was not keyboard accessible.");
      }
      invoke.Invoke();
   }



   /// <summary>Finds realized native controls without external input or desktop automation.</summary>
   private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
   {
      for(int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
      {
         DependencyObject child = VisualTreeHelper.GetChild(root, index);
         yield return child;
         foreach(DependencyObject descendant in Descendants(child))
         {
            yield return descendant;
         }
      }
   }



   /// <summary>Fails the isolated host promptly when a native image or scrolling event never completes.</summary>
   private static async Task WaitAsync(Func<bool> condition)
   {
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
      while(!condition())
      {
         await Task.Delay(25, timeout.Token);
      }
   }
}
