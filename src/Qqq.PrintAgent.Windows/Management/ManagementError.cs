/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// Why a request failed. Clients decide by <see cref="Code"/>, one of the <see cref="ManagementErrorCode"/> values; the
/// message is for people and no client matches its text. A client that meets a code it does not know shows the message
/// and treats the failure as it would <see cref="ManagementErrorCode.InternalError"/>. Details are present only for the
/// codes whose table entry says so, and their type is fixed per code (<see cref="ManagementErrorCode.DetailsType"/>).
/// </summary>
public sealed record ManagementError(string Code, string Message, ManagementPayload? Details = null)
{
   /// <summary>Shows the code and the message, not the details, which are read through <see cref="ManagementJson"/> as their own type.</summary>
   public override string ToString() => $"{Code}: {Message}";
}
