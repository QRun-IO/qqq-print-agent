/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Service;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// The messages the window shows about the local service itself and about the result of an operator action. They are
/// built here from typed results, so "what does the operator read when this fails" is answered by a test rather than by
/// reading event handlers.
/// </summary>
public static class ShellFeedback
{
   /// <summary>
   /// The message for a service that cannot be reached. It leads with the Windows service's own guidance when that is known
   /// and always says where the controls are and that this window needs administrator access.
   /// </summary>
   public static FeedbackMessage ServiceUnavailable(string? serviceGuidance)
      => new("Service unavailable", (serviceGuidance ?? "The local service could not be reached.") + " Open Settings for service controls, Windows Services or the installer. This administration window needs administrator access.", FeedbackSeverity.Error);



   /// <summary>The message for a status request the service answered with an error; the service's own text is the explanation.</summary>
   public static FeedbackMessage ServiceProblem(string message) => new("Service reported a problem", message, FeedbackSeverity.Error);



   /// <summary>The message for a status reply this companion cannot read, which in practice means the companion and service versions differ.</summary>
   public static FeedbackMessage UnreadableStatus { get; } = new("Cannot read service response", "Install matching versions of print agent companion and service, then refresh.", FeedbackSeverity.Error);



   /// <summary>True for the service-level messages that a good status refresh makes obsolete, so the window closes them itself instead of leaving an old failure on screen.</summary>
   public static bool ClearsOnStatus(string? title) => title is "Checking service" or "Service unavailable" or "Cannot read service response";



   /// <summary>The message after asking the service to reconnect: the service's sentence, whatever it says.</summary>
   public static FeedbackMessage ReconnectRequested(string reply) => new("Reconnect requested", reply, FeedbackSeverity.Informational);



   /// <summary>The message when sharing a printer was not confirmed by the service.</summary>
   public static FeedbackMessage ExposureNotSaved { get; } = new("Printer setting not saved", "The service did not confirm this change. Refresh and try again.", FeedbackSeverity.Error);



   /// <summary>
   /// The message for a state-changing request. Saving details or log settings gets its own confirmation sentence and every
   /// other action shows the service's reply. A refusal shows the service's reason. No usable answer is reported as "result
   /// could not be confirmed", because a timed-out request may already have reached the server or the printer and must not
   /// be blindly repeated.
   /// </summary>
   public static FeedbackMessage ForAction(string operation, ServiceResult<string> result)
   {
      ArgumentNullException.ThrowIfNull(result);
      return result.Outcome switch
      {
         ServiceOutcome.Success => new FeedbackMessage("Request completed", operation switch
         {
            "set-device-profile" => "Computer details saved. They will synchronize when the service next contacts the server.",
            "set-printer-profile" => "Printer details saved. They will synchronize when the service next contacts the server.",
            "set-log-settings" => "Diagnostic log settings saved for the service and companion.",
            _ => result.Payload ?? string.Empty,
         }, FeedbackSeverity.Success),
         ServiceOutcome.Rejected => ActionRejected(result.Message),
         _ => new FeedbackMessage("Result could not be confirmed", "Check Jobs and Logs before trying again. A timed-out request may already have reached the server or the printer.", FeedbackSeverity.Warning),
      };
   }



   /// <summary>The message for a request the service refused, with the service's reason as the explanation.</summary>
   public static FeedbackMessage ActionRejected(string message) => new("Action could not be completed", message, FeedbackSeverity.Error);
}
