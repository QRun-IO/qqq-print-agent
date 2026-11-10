/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// A server's development-only certificate exception: whether it is on, and the two addresses it covers and no others, the
/// server's own and the sign-in provider that server names. The provider's address is absent until the service has read it
/// from the server. A client shows this for as long as the exception is on, so a development setting is not forgotten.
/// </summary>
public sealed record CertificateExceptionState(bool Enabled, string ServerOrigin, string? ProviderOrigin = null);
