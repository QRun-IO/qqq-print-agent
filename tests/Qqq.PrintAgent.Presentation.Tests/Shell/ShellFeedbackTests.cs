/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.Shell;

/// <summary>The messages about the local service and about the result of an operator action, and the footer's transport texts.</summary>
public sealed class ShellFeedbackTests
{
   /// <summary>Runs one action against a scripted service and returns the message the window would show for it.</summary>
   private static async Task<FeedbackMessage> ActionAsync(string operation, Func<Task<string>> reply)
   {
      ServiceClient client = new(_ => reply(), new RecordingLog(), "QQQ");
      return ShellFeedback.ForAction(operation, await client.SendAsync(new PipeRequest(operation)));
   }



   /// <summary>An unreachable service is reported with the Windows service's own guidance when it is known, and with a general sentence when it is not; both say where the controls are.</summary>
   [Fact]
   public void UnreachableService_LeadsWithTheWindowsServiceGuidance()
   {
      const string Where = " Open Settings for service controls, Windows Services or the installer. This administration window needs administrator access.";

      Assert.Equal(new FeedbackMessage("Service unavailable", "The service is stopped. Start it from Settings." + Where, FeedbackSeverity.Error), ShellFeedback.ServiceUnavailable("The service is stopped. Start it from Settings."));
      Assert.Equal(new FeedbackMessage("Service unavailable", "The local service could not be reached." + Where, FeedbackSeverity.Error), ShellFeedback.ServiceUnavailable(null));
   }



   /// <summary>A status error shows the service's text, an unreadable status asks for matching versions, a reconnect shows the service's sentence, and an unconfirmed sharing change asks for a refresh.</summary>
   [Fact]
   public void ServiceMessages_SayWhatHappenedAndWhatToDo()
   {
      Assert.Equal(new FeedbackMessage("Service reported a problem", "The journal is locked.", FeedbackSeverity.Error), ShellFeedback.ServiceProblem("The journal is locked."));
      Assert.Equal(new FeedbackMessage("Cannot read service response", "Install matching versions of print agent companion and service, then refresh.", FeedbackSeverity.Error), ShellFeedback.UnreadableStatus);
      Assert.Equal(new FeedbackMessage("Reconnect requested", "Reconnect requested.", FeedbackSeverity.Informational), ShellFeedback.ReconnectRequested("Reconnect requested."));
      Assert.Equal(new FeedbackMessage("Printer setting not saved", "The service did not confirm this change. Refresh and try again.", FeedbackSeverity.Error), ShellFeedback.ExposureNotSaved);
   }



   /// <summary>Only the service-level messages are closed by a good status refresh; the result of an operator action stays until the operator has read it.</summary>
   [Theory]
   [InlineData("Checking service", true)]
   [InlineData("Service unavailable", true)]
   [InlineData("Cannot read service response", true)]
   [InlineData("Request completed", false)]
   [InlineData(null, false)]
   public void GoodStatus_ClosesOnlyServiceLevelMessages(string? title, bool expected)
      => Assert.Equal(expected, ShellFeedback.ClearsOnStatus(title));



   /// <summary>A completed action shows its own confirmation for saved details and log settings, and the service's sentence for everything else.</summary>
   [Theory]
   [InlineData("set-device-profile", "Computer details saved. They will synchronize when the service next contacts the server.")]
   [InlineData("set-printer-profile", "Printer details saved. They will synchronize when the service next contacts the server.")]
   [InlineData("set-log-settings", "Diagnostic log settings saved for the service and companion.")]
   [InlineData("retry-job", "A new job was created.")]
   public async Task CompletedAction_ConfirmsInWords(string operation, string expected)
      => Assert.Equal(new FeedbackMessage("Request completed", expected, FeedbackSeverity.Success), await ActionAsync(operation, () => Task.FromResult("A new job was created.")));



   /// <summary>A refused action shows the service's reason without the error marker.</summary>
   [Fact]
   public async Task RefusedAction_ShowsTheServiceReason()
      => Assert.Equal(new FeedbackMessage("Action could not be completed", "The job is already printing.", FeedbackSeverity.Error), await ActionAsync("cancel-job", () => Task.FromResult("Error: The job is already printing.")));



   /// <summary>An action without a usable answer is reported as unconfirmed with a warning not to repeat it blindly, whatever the failure was.</summary>
   [Fact]
   public async Task UnansweredAction_IsUnconfirmedRatherThanFailed()
   {
      FeedbackMessage expected = new("Result could not be confirmed", "Check Jobs and Logs before trying again. A timed-out request may already have reached the server or the printer.", FeedbackSeverity.Warning);

      Assert.Equal(expected, await ActionAsync("test-print", () => Task.FromException<string>(new OperationCanceledException())));
      Assert.Equal(expected, await ActionAsync("test-print", () => Task.FromException<string>(new InvalidDataException("too large"))));
   }



   /// <summary>Without an observation every transport text says it is unavailable.</summary>
   [Fact]
   public void MissingTransport_SaysUnavailableEverywhere()
   {
      var status = TransportStatus.From(null, TestData.Time);

      Assert.Same(TransportStatus.Unavailable, status);
      Assert.Equal("WebSocket status unavailable", status.StateText);
      Assert.Equal("Traffic unavailable", status.TrafficText);
      Assert.Equal("A current WebSocket observation is not available from the local service.", status.StateToolTip);
      Assert.Equal("The local service has not supplied a current traffic measurement.", status.TrafficToolTip);
      Assert.Equal("Traffic unavailable.", status.TrafficHelpText);
   }



   /// <summary>An observation shows the WebSocket state, the two rates, and totals since the service started in local time, with an explanation of what is counted.</summary>
   [Fact]
   public void Transport_ShowsStateRatesAndTotals()
   {
      AgentTransportSnapshot transport = new("WSS", "CONNECTED", UploadedBytes: 2048, DownloadedBytes: 3 * 1024 * 1024, UploadBytesPerSecond: 0, DownloadBytesPerSecond: 1024, new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero));

      var status = TransportStatus.From(transport, TestData.Time);

      Assert.Equal("WebSocket connected (WSS)", status.StateText);
      Assert.Equal("↓ 1 KiB/s · ↑ 0 B/s", status.TrafficText);
      Assert.Equal("WebSocket connected (WSS). Dispatch permission and printer health are shown separately above.", status.StateToolTip);
      Assert.Equal("Received 3 MiB · Sent 2 KiB since service start (11/10/2026 10:00:00).", status.TrafficHelpText);
      Assert.Equal("Received 3 MiB · Sent 2 KiB since service start (11/10/2026 10:00:00).\nQQQ service payload bytes, including documents and WebSocket messages; excludes headers, protocol framing and TLS. Rates cover the latest two seconds and refresh locally every second. Totals reset when the service restarts.", status.TrafficToolTip);
   }
}
