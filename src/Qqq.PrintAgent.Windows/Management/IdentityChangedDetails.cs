/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Serialization;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The details of <see cref="ManagementErrorCode.IdentityChanged"/>: the account the pairing is approved for, so the
/// operator can be told which account to sign in as. Both values were supplied by the server when the pairing was
/// approved, so they are never held as given. The record has one constructor, which gives each value the form of an
/// identity value on the wire (<see cref="ManagementText.Identity"/>: printable, and cut at
/// <see cref="ManagementProtocol.MaximumIdentityCharacters"/> exactly). <see cref="For"/> builds the record in code
/// through it, and the serializer reads the wire through it, so a client holds bounded values even when a service did
/// not send them so. A value the pairing does not record is absent: the member is null and is left out of the text,
/// and no words stand in for it, so a client never has to tell a missing account from one with a peculiar name.
/// </summary>
public sealed record IdentityChangedDetails
{
   /// <summary>Takes the two values and gives each the form the wire carries. <see cref="For"/> and the serializer are its only callers.</summary>
   [JsonConstructor]
   private IdentityChangedDetails(string? approvedPrincipalId, string? approvedTenantId)
   {
      ApprovedPrincipalId = ManagementText.Identity(approvedPrincipalId);
      ApprovedTenantId = ManagementText.Identity(approvedTenantId);
   }



   /// <summary>The principal the pairing is approved for, printable and bounded, or null when the pairing records none.</summary>
   public string? ApprovedPrincipalId { get; }



   /// <summary>The tenant the pairing is approved for, printable and bounded, or null when the pairing records none.</summary>
   public string? ApprovedTenantId { get; }



   /// <summary>Builds the details from the approved account as the pairing recorded it. A value that is missing or has nothing to show stays absent.</summary>
   public static IdentityChangedDetails For(string? approvedPrincipalId, string? approvedTenantId) => new(approvedPrincipalId, approvedTenantId);
}
