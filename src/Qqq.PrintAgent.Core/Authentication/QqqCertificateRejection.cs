/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.Security;

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// Why a server certificate was refused and which certificate it was, so the wizard can show the operator the issuer and
/// thumbprint they would be trusting before they allow an exception. All of it is public certificate data; a certificate
/// the server did not present leaves the identity fields null.
/// </summary>
public sealed record QqqCertificateRejection(SslPolicyErrors Errors, string? Subject, string? Issuer, DateTime? NotBeforeUtc, DateTime? NotAfterUtc, string? Thumbprint)
{
   /// <summary>True when Windows does not trust the issuing authority, the one case an administrator may choose to allow for a development server.</summary>
   public bool IsUntrustedAuthority => Errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors);



   /// <summary>True when the certificate was issued for a different host name or address than the one entered.</summary>
   public bool IsNameMismatch => Errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch);
}
