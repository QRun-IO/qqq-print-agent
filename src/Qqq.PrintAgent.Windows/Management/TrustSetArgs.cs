/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// Arguments of <see cref="ManagementOperations.TrustSet"/>: whether this server's development-only certificate
/// exception is on, and, when a client switches it on after checking the server, the sign-in provider's origin that
/// <see cref="ManagementOperations.ServersCheck"/> reported (<see cref="ServerCheckResult.ProviderOrigin"/>). The
/// addresses the exception covers are not taken from a client: the service records the server's own origin and the
/// sign-in provider that server names, from its own read. The provider's origin is carried for comparison only, as in
/// <see cref="ServersAddArgs"/> and for the same reason: the operator accepted the exception for the provider the
/// check showed, a server whose certificate is not verified could name another when the service reads it again, and
/// the exception must not come to cover an origin the operator did not see. When the origin is given and the service
/// reads another, nothing is changed and the answer is <see cref="ManagementErrorCode.ProviderChanged"/>. It is
/// optional: a request that switches the exception off carries none, and neither does a client that ran no check.
/// </summary>
public sealed record TrustSetArgs(bool AcceptCertificateException, string? ProviderOrigin = null)
{
   /// <summary>
   /// True when this request does not contradict the sign-in provider the service read from the server
   /// (<see cref="SignInProviderOrigin.Agrees"/>): it carries the same text, or carries no origin. When it is false
   /// the service changes nothing and answers <see cref="ManagementErrorCode.ProviderChanged"/>.
   /// </summary>
   public bool ProviderAgrees(string? readByTheService) => SignInProviderOrigin.Agrees(ProviderOrigin, readByTheService);
}
