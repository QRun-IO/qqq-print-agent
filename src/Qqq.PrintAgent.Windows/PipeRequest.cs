/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// One management request from the companion or command line to the service over the local pipe. Only the fields an operation uses are
/// set, and credentials travel only in the typed session field so they never appear in text.
/// </summary>
public sealed record PipeRequest(string Operation, string? ServerUrl = null, string? EnrollmentCode = null, string? PrinterId = null, bool? Exposed = null,
   string? JobId = null, string? Format = null, bool ConfirmDuplicateRisk = false, JobQuery? JobQuery = null, LogSettings? LogSettings = null,
   Qqq.PrintAgent.Core.Printing.DeviceProfile? DeviceProfile = null, PrinterProfile? PrinterProfile = null, string? TestProfile = null,
   Qqq.PrintAgent.Core.Authentication.QqqSessionCredential? SessionCredential = null, bool ClaimLegacyDevice = false, bool StartNewPairing = false)
{
   /// <summary>Returns a fixed label instead of the generated record text, so logging a request can never reveal a session credential or enrollment code.</summary>
   public override string ToString() => "Print agent management request (credentials redacted)";
}



