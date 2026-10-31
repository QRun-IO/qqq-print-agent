/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.Cryptography;
using System.Text.Json;

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>Stable server-derived identity, separate from rotating credentials. It is not an authorization grant.</summary>
public sealed record QqqSessionBinding(Uri Origin, string PrincipalId, string TenantId, string? DeviceId)
{
   public bool HasApprovedDevice => !string.IsNullOrEmpty(DeviceId);

   /// <summary>Unambiguous identity for a host's journal migration; session and device secrets are excluded.</summary>
   public string IdentityKey => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
      new[] { Origin.AbsoluteUri, PrincipalId, TenantId, DeviceId })));
}
