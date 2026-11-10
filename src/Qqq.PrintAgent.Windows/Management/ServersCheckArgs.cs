/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// Arguments of <see cref="ManagementOperations.ServersCheck"/>: the address to check and whether the operator has accepted
/// the development-only certificate exception for it. For an address that is already a server the stored exception is
/// used, which is how a repair checks again. Nothing is stored by a check.
/// </summary>
public sealed record ServersCheckArgs(string Address, bool AcceptCertificateException = false);
