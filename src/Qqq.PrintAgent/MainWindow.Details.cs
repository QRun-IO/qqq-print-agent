using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Qqq.PrintAgent;

public sealed partial class MainWindow
{
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

   private static void ResizeWideDialog(ScrollViewer scroll, XamlRoot root)
   {
      scroll.Width = Math.Max(180, Math.Min(900, root.Size.Width - 48) - 48);
      scroll.MaxHeight = Math.Min(640, Math.Max(180, root.Size.Height - 200));
   }

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
