/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>Plain-language wording for the server's remote controls, kept in one place so the footer, status text and tests say the same thing.</summary>
public static class AgentControlLabels
{
   /// <summary>Words the negotiated remote-control support as available, unavailable on an older server, or still being checked.</summary>
   public static string Support(string value) => value switch
   {
      "SUPPORTED" => "available",
      "LEGACY" => "unavailable on this server",
      _ => "checking support",
   };



   /// <summary>Names a remote action in words an operator reads, and "Unknown action" for anything this agent does not implement.</summary>
   public static string Action(string value) => value switch
   {
      "REFRESH_INVENTORY" => "Refresh printers",
      "CANCEL_SPOOL" => "Cancel one spooler job",
      _ => "Unknown action",
   };



   /// <summary>Words a remote action's recorded outcome honestly, including that printed pages cannot be recalled and that an unknown outcome needs inspection before any retry; a missing outcome reads "No result recorded".</summary>
   public static string Outcome(string? value) => value switch
   {
      "INVENTORY_REFRESHED" => "Printers refreshed",
      "CANCEL_REQUESTED" => "Cancellation requested; printed pages cannot be recalled",
      "ALREADY_ABSENT" => "Already absent or completed",
      "OWNERSHIP_MISMATCH" => "Refused: spooler ownership was not verified",
      "UNSUPPORTED" => "Unsupported action",
      "FAILED" => "Failed; inspect status",
      "EXPIRED" => "Expired without execution",
      "OUTCOME_UNKNOWN" => "Outcome unknown; inspect output before retrying",
      _ => "No result recorded",
   };
}
