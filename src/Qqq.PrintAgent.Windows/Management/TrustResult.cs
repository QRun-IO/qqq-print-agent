/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The result of <see cref="ManagementOperations.TrustGet"/> and <see cref="ManagementOperations.TrustSet"/>: the server's
/// certificate exception with the two addresses it covers, and the certificate Windows last refused for the server, when
/// there is one, so the operator sees what the exception accepts.
/// </summary>
public sealed record TrustResult(CertificateExceptionState CertificateException, PresentedCertificate? Certificate = null);
