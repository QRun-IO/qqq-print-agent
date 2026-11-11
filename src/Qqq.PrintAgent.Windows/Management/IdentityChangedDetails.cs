/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Serialization;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The details of <see cref="ManagementErrorCode.IdentityChanged"/>: the account the pairing is approved for, so the
/// operator can be told which account to sign in as. Both values were supplied by the server when the pairing was
/// approved, so they are never sent as given. <see cref="For"/> is the only way to build this record in code, and it
/// puts both values through <see cref="QqqCompatibilityProblem.Printable"/>, the one definition Core uses for text a
/// server supplied, cut at <see cref="ManagementProtocol.MaximumIdentityCharacters"/>. The other constructor is
/// private and belongs to the serializer: it reads the two values a service wrote, which <see cref="For"/> had already
/// made printable.
/// </summary>
public sealed record IdentityChangedDetails
{
   /// <summary>Takes the two values as they are written on the wire. Only the serializer calls it.</summary>
   [JsonConstructor]
   private IdentityChangedDetails(string approvedPrincipalId, string approvedTenantId)
   {
      ApprovedPrincipalId = approvedPrincipalId;
      ApprovedTenantId = approvedTenantId;
   }



   /// <summary>The principal the pairing is approved for, printable and bounded.</summary>
   public string ApprovedPrincipalId { get; }



   /// <summary>The tenant the pairing is approved for, printable and bounded.</summary>
   public string ApprovedTenantId { get; }



   /// <summary>Builds the details from the approved account as the pairing recorded it, making each value printable and cutting it at <see cref="ManagementProtocol.MaximumIdentityCharacters"/> UTF-16 units. A value that is missing is shown as such.</summary>
   public static IdentityChangedDetails For(string? approvedPrincipalId, string? approvedTenantId)
      => new(QqqCompatibilityProblem.Printable(approvedPrincipalId, ManagementProtocol.MaximumIdentityCharacters), QqqCompatibilityProblem.Printable(approvedTenantId, ManagementProtocol.MaximumIdentityCharacters));
}
