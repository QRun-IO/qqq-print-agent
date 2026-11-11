/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.Security;

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// Why a server certificate was refused, which certificate it was, which address presented it and when, so the wizard
/// can show the operator the issuer and thumbprint they would be trusting before they allow an exception. One session
/// client talks to its server and to the sign-in provider that server names, so the certificate alone does not say
/// which of the two presented it: <paramref name="Origin"/> does. It is the origin of the address the request or the
/// socket upgrade was going to, with the host as its ASCII name (see <see cref="QqqOrigin.Of"/>), and null only for
/// an address whose host has no such name. <paramref name="ObservedAt"/> is the session client's clock at the
/// refusal. The rest is public certificate data; a certificate the server did not present leaves the identity fields
/// null.
/// </summary>
public sealed record QqqCertificateRejection(SslPolicyErrors Errors, string? Subject, string? Issuer, DateTime? NotBeforeUtc, DateTime? NotAfterUtc, string? Thumbprint,
   Uri? Origin, DateTimeOffset ObservedAt)
{
   /// <summary>True when Windows does not trust the issuing authority, the one case an administrator may choose to allow for a development server.</summary>
   public bool IsUntrustedAuthority => Errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors);



   /// <summary>True when the certificate was issued for a different host name or address than the one entered.</summary>
   public bool IsNameMismatch => Errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch);
}
