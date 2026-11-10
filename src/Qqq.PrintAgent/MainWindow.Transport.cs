/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.Shell;
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
   /// error, an unreadable reply or an unreachable service shows "unavailable" rather than old numbers, and an overlapping
   /// refresh is skipped, so a slow service cannot queue up requests.
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
         ServiceResult<AgentTransportSnapshot> result = await _service.TransportAsync();
         if(!_updatesClosed)
         {
            RenderTransport(result.Payload);
         }
      }
      finally
      {
         _transportRefreshing = false;
      }
   }



   /// <summary>
   /// Shows the connection state and the download and upload rates, with the exact totals and what they measure in the tooltip
   /// and as accessible help text. The texts come from <see cref="TransportStatus"/>, which says "unavailable" when there is
   /// no observation instead of leaving a stale figure on screen.
   /// </summary>
   private void RenderTransport(AgentTransportSnapshot? transport)
   {
      var status = TransportStatus.From(transport, _time);
      TransportStateText.Text = status.StateText;
      TransportTrafficText.Text = status.TrafficText;
      ToolTipService.SetToolTip(TransportStateText, status.StateToolTip);
      ToolTipService.SetToolTip(TransportTrafficText, status.TrafficToolTip);
      AutomationProperties.SetHelpText(TransportTrafficText, status.TrafficHelpText);
   }
}
