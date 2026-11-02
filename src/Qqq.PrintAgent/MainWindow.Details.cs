/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Qqq.PrintAgent;

/// <summary>
/// Dialog and double-click part of the main window: a wide dialog that follows the window's size, and the double-click on a
/// printer or job row that opens its settings or full details without stealing clicks meant for buttons or selectable text.
/// </summary>
public sealed partial class MainWindow
{
   /// <summary>
   /// Creates a dialog up to 900 units wide whose scrolling body is resized to the window while it is open (the resize handler is
   /// attached on open and removed on close, so a closed dialog leaves nothing subscribed). Long content scrolls instead of
   /// stretching the dialog past the window.
   /// </summary>
   private ContentDialog CreateWideDialog(string title, UIElement body)
   {
      XamlRoot root = Content.XamlRoot;
      ScrollViewer scroll = new() { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
      var dialog = new ContentDialog
      {
         Title = title,
         CloseButtonText = "Close",
         XamlRoot = Content.XamlRoot,
         HorizontalContentAlignment = HorizontalAlignment.Stretch,
         Content = scroll,
      };
      dialog.Resources["ContentDialogMinWidth"] = 0d;
      dialog.Resources["ContentDialogMaxWidth"] = 900d;
      void Resize(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeWideDialog(scroll, root);
      dialog.Opened += (sender, args) =>
      {
         ResizeWideDialog(scroll, root);
         root.Changed += Resize;
      };
      dialog.Closed += (sender, args) => root.Changed -= Resize;
      ResizeWideDialog(scroll, root);
      return dialog;
   }



   /// <summary>Fits the dialog body to the current window, never wider than the dialog maximum or taller than 640, with a floor of 180 so a tiny window still shows content.</summary>
   private static void ResizeWideDialog(ScrollViewer scroll, XamlRoot root)
   {
      scroll.Width = Math.Max(180, Math.Min(900, root.Size.Width - 48) - 48);
      scroll.MaxHeight = Math.Min(640, Math.Max(180, root.Size.Height - 200));
   }



   /// <summary>Opens a printer's settings when its row is double-clicked, selecting the row first; clicks on buttons, text boxes or selectable text are ignored so those controls keep working, and nothing opens while a request runs.</summary>
   private async void Printers_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
   {
      PrinterRowView? row = FindDoubleTappedRow<PrinterRowView>(e.OriginalSource);
      if(_busy || row?.Snapshot is not { } printer)
      {
         return;
      }
      e.Handled = true;
      PrintersList.SelectedItem = row;
      await EditPrinterAsync(printer);
   }



   /// <summary>Opens a job's full details when its row is double-clicked, with the same exclusions as the printer list.</summary>
   private async void Jobs_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
   {
      JobRowView? row = FindDoubleTappedRow<JobRowView>(e.OriginalSource);
      if(_busy || row is null)
      {
         return;
      }
      e.Handled = true;
      JobsList.SelectedItem = row;
      await JobDetailsAsync(row.Snapshot);
   }



   /// <summary>
   /// Walks up from the element that was double-clicked to find its row. It returns null if it first meets a button, text or
   /// password box, selectable text or an inline details panel, because a double-click there belongs to that control (for
   /// example selecting a word), not to the row.
   /// </summary>
   private static T? FindDoubleTappedRow<T>(object source) where T : FrameworkElement
   {
      for(var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
      {
         if(current is ButtonBase or TextBox or PasswordBox or TextBlock { IsTextSelectionEnabled: true }
            or FrameworkElement { Tag: "PrinterInlineDetails" or "JobInlineDetails" })
         {
            return null;
         }
         if(current is T row)
         {
            return row;
         }
         if(current is ListViewItem item && item.Content is T content)
         {
            return content;
         }
      }
      return null;
   }
}
