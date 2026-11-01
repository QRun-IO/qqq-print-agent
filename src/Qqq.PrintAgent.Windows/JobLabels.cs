/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>Shared visible job labels used by the companion and server-side local sorting.</summary>
public static class JobLabels
{
   /// <summary>Turns a status code into the plain wording shown to the operator, with a "Reviewed" suffix when they have acknowledged it. An unknown code reads "Status unavailable" rather than echoing raw text, and the outcome-unknown wording tells them to check the printer before retrying.</summary>
   public static string Status(string state, bool reviewed = false)
   {
      string label = state switch
      {
         "READY" => "Ready",
         "OFFLINE" => "Offline",
         "PAUSED" => "Paused",
         "ERROR" => "Needs attention",
         "QUEUED" => "Queued",
         "CLAIMED" => "Assigned to this computer",
         "RECEIVED" => "Received",
         "SUBMITTING" => "Sending to printer",
         "SPOOLED" => "Sent to printer",
         "SPOOLER_COMPLETED" => "Windows completed the job",
         "FAILED" => "Failed",
         "OUTCOME_UNKNOWN" => "Check printer before retrying",
         _ => "Status unavailable",
      };
      return label + (reviewed ? " · Reviewed" : "");
   }
}
