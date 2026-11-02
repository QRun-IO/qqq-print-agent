/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

/// <summary>
/// Connection footer part of the main window: the WebSocket state and live traffic rates. It asks the local service for a
/// small observation once a second; this is a local read, not a request to QQQ, so it adds no job or control polling.
/// </summary>
public sealed partial class MainWindow
{
   private readonly DispatcherTimer _transportTimer = new() { Interval = TimeSpan.FromSeconds(1) };
   private bool _transportStarted;
   private bool _transportRefreshing;



   /// <summary>Starts the one-second refresh timer once; later calls do nothing, so the timer is never started twice.</summary>
   private void InitializeTransportStatus()
   {
      if(_transportStarted)
      {
         return;
      }
      _transportStarted = true;
      _transportTimer.Tick += async (_, _) => await RefreshTransportAsync();
      _transportTimer.Start();
   }



   /// <summary>
   /// Reads the transport observation from the service and shows it. A reply that arrives after the window closed is dropped, an
   /// error or unreadable reply shows "unavailable" rather than old numbers, and an overlapping refresh is skipped, so a slow
   /// service cannot queue up requests.
   /// </summary>
   private async Task RefreshTransportAsync()
   {
      if(_updatesClosed || _transportRefreshing)
      {
         return;
      }
      _transportRefreshing = true;
      try
      {
         string response = await _sendRequest(new PipeRequest("transport"));
         if(!_updatesClosed)
         {
            RenderTransport(response.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ? null
               : JsonSerializer.Deserialize<AgentTransportSnapshot>(response, PrintApiClient.JsonOptions));
         }
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException)
      {
         if(!_updatesClosed)
         {
            RenderTransport(null);
         }
      }
      finally
      {
         _transportRefreshing = false;
      }
   }



   /// <summary>
   /// Shows the connection state and the download and upload rates, with the exact totals and what they measure (service
   /// payload bytes, not headers or TLS) in the tooltip and as accessible help text. With no observation it says so plainly
   /// instead of leaving a stale figure on screen.
   /// </summary>
   private void RenderTransport(AgentTransportSnapshot? transport)
   {
      if(transport is null)
      {
         TransportStateText.Text = "WebSocket status unavailable";
         TransportTrafficText.Text = "Traffic unavailable";
         ToolTipService.SetToolTip(TransportStateText, "A current WebSocket observation is not available from the local service.");
         ToolTipService.SetToolTip(TransportTrafficText, "The local service has not supplied a current traffic measurement.");
         AutomationProperties.SetHelpText(TransportTrafficText, "Traffic unavailable.");
         return;
      }
      TransportStateText.Text = TransportFormatting.State(transport.Protocol, transport.State);
      TransportTrafficText.Text = $"↓ {TransportFormatting.Rate(transport.DownloadBytesPerSecond)} · ↑ {TransportFormatting.Rate(transport.UploadBytesPerSecond)}";
      string since = transport.TotalsSinceUtc.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);
      string totals = $"Received {TransportFormatting.Bytes(transport.DownloadedBytes)} · Sent {TransportFormatting.Bytes(transport.UploadedBytes)} since service start ({since}).";
      AutomationProperties.SetHelpText(TransportTrafficText, totals);
      ToolTipService.SetToolTip(TransportStateText, TransportStateText.Text + ". Dispatch permission and printer health are shown separately above.");
      ToolTipService.SetToolTip(TransportTrafficText, totals + "\nQQQ service payload bytes, including documents and WebSocket messages; excludes headers, protocol framing and TLS. Rates cover the latest two seconds and refresh locally every second. Totals reset when the service restarts.");
   }
}
