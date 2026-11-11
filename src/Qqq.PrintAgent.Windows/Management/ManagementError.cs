/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// Why a request failed. Clients decide by <see cref="Code"/>, one of the <see cref="ManagementErrorCode"/> values; the
/// message is for people and no client matches its text. A client that meets a code it does not know shows the message
/// and treats the failure as it would <see cref="ManagementErrorCode.InternalError"/>. Details are present only for the
/// codes whose table entry says so, and their type is fixed per code (<see cref="ManagementErrorCode.DetailsType"/>).
/// The message may repeat what a server said, so it is never held as given: the one constructor makes it printable and
/// cuts it at <see cref="ManagementProtocol.MaximumMessageCharacters"/>, and nothing can change it afterwards, because
/// no member can be set once the error exists (a <c>with</c> expression can only copy it). That holds for an error the
/// service builds, by whatever route, and for one a client reads from the wire, so text a server supplied reaches
/// neither the wire nor a client's screen raw.
/// </summary>
public sealed record ManagementError
{
   /// <summary>Builds an error. The message is made printable and cut here, the only place an error's message is ever set.</summary>
   public ManagementError(string code, string message, ManagementPayload? details = null)
   {
      ArgumentNullException.ThrowIfNull(code);
      Code = code;
      Message = QqqCompatibilityProblem.Printable(message, ManagementProtocol.MaximumMessageCharacters);
      Details = details;
   }



   /// <summary>What went wrong, as one of the <see cref="ManagementErrorCode"/> values. Clients decide by this.</summary>
   public string Code { get; }



   /// <summary>What went wrong, for people: printable, and bounded by <see cref="ManagementProtocol.MaximumMessageCharacters"/>.</summary>
   public string Message { get; }



   /// <summary>The details the code's table entry promises, or null for a code that carries none.</summary>
   public ManagementPayload? Details { get; }



   /// <summary>Shows the code and the message, not the details, which are read through <see cref="ManagementJson"/> as their own type.</summary>
   public override string ToString() => $"{Code}: {Message}";
}
