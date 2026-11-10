/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// The states a job passes through, by the exact names the wire contract and the journal use. They are named once here
/// so a state is never spelled by hand where a typing mistake would silently fail to match, and so the rule for which
/// states are final has one home.
/// </summary>
public static class PrintJobState
{
   /// <summary>The server handed the job to this device; nothing is recorded locally yet.</summary>
   public const string Claimed = "CLAIMED";

   /// <summary>The agent recorded the job and told the server it has it. No bytes have gone to a printer.</summary>
   public const string Received = "RECEIVED";

   /// <summary>The agent is about to hand the document to the printer. From here on, an interruption leaves the outcome unknown.</summary>
   public const string Submitting = "SUBMITTING";

   /// <summary>Windows accepted the document into the spooler. That is not the same as paper coming out.</summary>
   public const string Spooled = "SPOOLED";

   /// <summary>The spooler reported the job complete.</summary>
   public const string SpoolerCompleted = "SPOOLER_COMPLETED";

   /// <summary>The job was refused or failed before any bytes could have reached a printer; submitting it again is safe.</summary>
   public const string Failed = "FAILED";

   /// <summary>Submission began and was interrupted, so the job may or may not have printed. It is never printed again automatically; an operator must look.</summary>
   public const string OutcomeUnknown = "OUTCOME_UNKNOWN";



   /// <summary>True for a state after which the agent never works on the job again: spooled, completed, failed or of unknown outcome.</summary>
   public static bool IsFinal(string? state) => state is Spooled or SpoolerCompleted or Failed or OutcomeUnknown;
}
