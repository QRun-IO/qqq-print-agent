/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The result of <see cref="ManagementOperations.TrustGet"/> and <see cref="ManagementOperations.TrustSet"/>: the server's
/// certificate exception with the two addresses it covers, and the certificate the service last saw Windows refuse for
/// the server or its sign-in provider, with the address that presented it and when, so the operator sees what the
/// exception accepts. It is what the service remembers from its own connections; reading it never contacts the server,
/// and it is absent when the service has seen no refusal since it started.
/// </summary>
public sealed record TrustResult(CertificateExceptionState CertificateException, PresentedCertificate? Certificate = null);
