/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The details of <see cref="ManagementErrorCode.EnrollmentUnconfirmed"/>: how far an enrollment got before its outcome
/// became unknown, the new device's identifier when the server had already answered with one, and whether printing is
/// blocked until the operator signs in again. It tells the operator what was left behind, so the next step is to check
/// again and not to spend another code.
/// </summary>
public sealed record EnrollmentUnconfirmedDetails(string Stage, bool PrintingBlocked, string? DeviceId = null)
{
   /// <summary>Stage: nothing changed on this computer. The server may still have used the code.</summary>
   public const string NothingChanged = "nothingChanged";

   /// <summary>Stage: the server bound the new device to the session; the stored pairing and session are still the old ones.</summary>
   public const string DeviceBound = "deviceBound";

   /// <summary>Stage: the new pairing and its session are stored but not active; a sign-in or a service restart finishes activating them.</summary>
   public const string Saved = "saved";

   /// <summary>Stage: the new pairing is stored and active; a later step failed.</summary>
   public const string Activated = "activated";
}
