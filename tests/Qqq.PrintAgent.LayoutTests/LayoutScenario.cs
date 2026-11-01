/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Qqq.PrintAgent;

internal static class LayoutScenario
{
   private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
   internal static async Task RunAsync(MainWindow window, SyntheticService service)
   {
      await WaitAsync(() => window.Content.XamlRoot is not null && service.Snapshots > 0);
      var root = (FrameworkElement)window.Content;
      await Task.Delay(250);
      var printers = (ListView)root.FindName("PrintersList");
      printers.SelectedItem = printers.Items.Cast<PrinterRowView>().Single(row => string.Equals(row.Snapshot?.PrinterId, SyntheticService.Printer.PrinterId, StringComparison.Ordinal));
      printers.ScrollIntoView(printers.SelectedItem);
      await Task.Delay(250);
      Button printButton = Descendants((PrinterRowView)printers.SelectedItem).OfType<Button>().Single(button => string.Equals(AutomationProperties.GetAutomationId(button), "PrintTest", StringComparison.Ordinal));
      printButton.Focus(FocusState.Programmatic);
      Invoke(printButton);
      await WaitAsync(() => Dialog(root.XamlRoot) is not null);
      ContentDialog dialog = Dialog(root.XamlRoot) ?? throw new InvalidOperationException("The real print dialog was not opened.");
      if(Program.RefreshDuringDialog)
      {
         await HoldForTimerRefreshAsync(root, printers, service);
      }
      await WaitAsync(() => Descendants(dialog).OfType<Button>().Any(button => string.Equals(button.Name, "PrimaryButton", StringComparison.Ordinal)));
      Button primary = Descendants(dialog).OfType<Button>().Single(button => string.Equals(button.Name, "PrimaryButton", StringComparison.Ordinal));
      Invoke(primary);
      await WaitAsync(() => service.Submissions == 1 && service.Snapshots >= 2 && Dialog(root.XamlRoot) is null);
      await Task.Delay(1000);
      root.UpdateLayout();
      Navigate(root, "NavigateJobs");
      await WaitAsync(() => service.JobQueries > 0);
      Navigate(root, "NavigatePrinters");
      await Task.Delay(250);
      root.UpdateLayout();
      await VerifyFooterAsync(root, service);
      var feedback = (InfoBar)root.FindName("FeedbackBar");
      if(!feedback.IsOpen || !string.Equals(feedback.Title, "Request completed", StringComparison.Ordinal))
      {
         throw new InvalidOperationException("The actual success feedback was not displayed.");
      }
      File.WriteAllText(Path.Combine(Program.EvidenceDirectory, "result.json"), JsonSerializer.Serialize(new
      {
         passed = true,
         nativeLayout = true,
         externalInputTested = false,
         service.Submissions,
         service.Snapshots,
         service.JobQueries,
         service.DialogSnapshots,
         service.TransportReads,
         trafficRateDecayVerified = true,
         serverLinkOpensSettingsOnly = true,
         heldForTimerRefresh = Program.RefreshDuringDialog,
         width = root.XamlRoot.Size.Width,
         height = root.XamlRoot.Size.Height,
         scale = root.XamlRoot.RasterizationScale,
         feedbackLength = feedback.Message.Length,
      }, s_json));
   }

   private static async Task HoldForTimerRefreshAsync(FrameworkElement root, ListView printers, SyntheticService service)
   {
      string previousFooter = ((TextBlock)root.FindName("FooterContact")).Text;
      service.DialogOpen = true;
      try
      {
         // Do not call RefreshAsync: only the real five-second DispatcherTimer drives this update.
         await Task.Delay(6500);
         await WaitAsync(() => service.DialogSnapshots > 0);
         var selected = (PrinterRowView)printers.SelectedItem;
         if(Dialog(root.XamlRoot) is null || service.Submissions != 0
            || !string.Equals(selected.Snapshot?.Description, SyntheticService.ChangedDescription, StringComparison.Ordinal)
            || string.Equals(((TextBlock)root.FindName("FooterContact")).Text, previousFooter, StringComparison.Ordinal))
         {
            throw new InvalidOperationException("The native timer did not render the changed synthetic snapshot while the dialog remained open.");
         }
         root.UpdateLayout();
      }
      finally
      {
         service.DialogOpen = false;
      }
   }

   private static void Navigate(FrameworkElement root, string id)
   {
      NavigationViewItem item = Descendants(root).OfType<NavigationViewItem>().Single(element => string.Equals(AutomationProperties.GetAutomationId(element), id, StringComparison.Ordinal));
      AutomationPeer peer = FrameworkElementAutomationPeer.CreatePeerForElement(item) ?? throw new InvalidOperationException("Navigation automation peer missing.");
      if(peer.GetPattern(PatternInterface.SelectionItem) is not ISelectionItemProvider selection)
      {
         throw new InvalidOperationException("Navigation selection provider missing.");
      }
      selection.Select();
   }

   private static void Invoke(Control button)
   {
      AutomationPeer peer = FrameworkElementAutomationPeer.CreatePeerForElement(button) ?? throw new InvalidOperationException("Button automation peer missing.");
      if(peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
      {
         throw new InvalidOperationException("Button invoke provider missing.");
      }
      invoke.Invoke();
   }

   private static async Task VerifyFooterAsync(FrameworkElement root, SyntheticService service)
   {
      var state = (TextBlock)root.FindName("TransportStateText");
      var traffic = (TextBlock)root.FindName("TransportTrafficText");
      service.RecordSyntheticTraffic();
      await WaitAsync(() => string.Equals(state.Text, "WebSocket connected (WSS)", StringComparison.Ordinal)
         && string.Equals(traffic.Text, "↓ 2 KiB/s · ↑ 1 KiB/s", StringComparison.Ordinal));
      string totals = AutomationProperties.GetHelpText(traffic);
      await Task.Delay(2500);
      await WaitAsync(() => string.Equals(traffic.Text, "↓ 0 B/s · ↑ 0 B/s", StringComparison.Ordinal));
      if(!string.Equals(AutomationProperties.GetHelpText(traffic), totals, StringComparison.Ordinal)
         || !totals.Contains("Received 4 KiB", StringComparison.Ordinal) || !totals.Contains("Sent 2 KiB", StringComparison.Ordinal))
      {
         throw new InvalidOperationException("Idle traffic totals changed or were not exposed accessibly.");
      }
      var link = (HyperlinkButton)root.FindName("FooterServer");
      if(link.NavigateUri is not null || !string.Equals(((TextBlock)root.FindName("FooterServerLabel")).Text, "synthetic.invalid", StringComparison.Ordinal))
      {
         throw new InvalidOperationException("The server link did not remain a safe local connection-settings action.");
      }
      var connection = (ContentDialog)root.FindName("ConnectionDialog");
      bool opened = false;
      bool closed = false;
      connection.Opened += (_, _) => opened = true;
      connection.Closed += (_, _) => closed = true;
      Invoke(link);
      // An inline XAML ContentDialog need not appear in GetOpenPopupsForXamlRoot.
      await WaitAsync(() => opened);
      // The inline dialog moves its template into an overlay; close through its native API.
      connection.Hide();
      await WaitAsync(() => closed);
      if(service.Submissions != 1)
      {
         throw new InvalidOperationException("The fixture submitted an unexpected number of synthetic actions.");
      }
   }

   private static ContentDialog? Dialog(XamlRoot root) => VisualTreeHelper.GetOpenPopupsForXamlRoot(root)
      .SelectMany(popup => Descendants(popup.Child).Prepend(popup.Child)).OfType<ContentDialog>().FirstOrDefault();

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

   private static async Task WaitAsync(Func<bool> condition)
   {
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
      while(!condition())
      {
         await Task.Delay(25, timeout.Token);
      }
   }
}
