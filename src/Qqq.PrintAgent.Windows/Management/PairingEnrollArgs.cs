/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// Arguments of <see cref="ManagementOperations.PairingEnroll"/>: the one-time enrollment code. The server's address is not
/// an argument: the request names the server and the service uses the address it has stored for it. The code is a secret,
/// so the text form of these arguments is fixed.
/// </summary>
public sealed record PairingEnrollArgs(string EnrollmentCode)
{
   /// <summary>Returns a fixed label, so logging the arguments can never reveal the enrollment code.</summary>
   public override string ToString() => "Arguments of pairing.enroll (enrollment code not shown)";
}
