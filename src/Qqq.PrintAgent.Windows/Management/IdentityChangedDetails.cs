/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The details of <see cref="ManagementErrorCode.IdentityChanged"/>: the account the pairing is approved for, so the
/// operator can be told which account to sign in as. Both values were supplied by the server when the pairing was
/// approved; the service puts them here in the printable, bounded form Core produces for text a server supplied, never
/// raw.
/// </summary>
public sealed record IdentityChangedDetails(string ApprovedPrincipalId, string ApprovedTenantId);
