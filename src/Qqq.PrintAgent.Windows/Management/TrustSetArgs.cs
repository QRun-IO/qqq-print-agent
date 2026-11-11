/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>Arguments of <see cref="ManagementOperations.TrustSet"/>: whether this server's development-only certificate exception is on. The addresses it covers are not arguments; the service records the server's own and the sign-in provider that server names.</summary>
public sealed record TrustSetArgs(bool AcceptCertificateException);
