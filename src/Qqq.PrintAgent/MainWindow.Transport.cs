using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

public sealed partial class MainWindow
{
   private readonly DispatcherTimer _transportTimer = new() { Interval = TimeSpan.FromSeconds(1) };
   private bool _transportStarted;
   private bool _transportRefreshing;

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
