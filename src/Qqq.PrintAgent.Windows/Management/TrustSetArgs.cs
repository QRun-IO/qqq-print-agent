/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// Arguments of <see cref="ManagementOperations.TrustSet"/>: whether this server's development-only certificate
/// exception is on, and the sign-in provider's origin that <see cref="ManagementOperations.ServersCheck"/> reported
/// (<see cref="ServerCheckResult.ProviderOrigin"/>), which states what the operator was shown. The addresses the
/// exception covers are not taken from a client: the service records the server's own origin and the sign-in provider
/// that server names, from its own read. The provider's origin is carried for comparison only, as in
/// <see cref="ServersAddArgs"/> and for the same reason: the operator accepted the exception for what the check
/// showed, a server whose certificate is not verified could name another provider when the service reads it again,
/// and the exception must not come to cover an origin the operator did not see. An absent origin states that the
/// check named no provider, and is compared like any other value. When the service reads something else, nothing is
/// changed and the answer is <see cref="ManagementErrorCode.ProviderChanged"/>. A client therefore checks the server
/// before it switches the exception on. Switching it off accepts nothing, so there is nothing to compare. A member
/// that is left out and one that is null both state that the check named no provider; text that names no origin
/// states nothing, and the record cannot be built with it, in code or from a request, whichever way the switch goes,
/// so such a request is refused as an invalid one when its arguments are read
/// (<see cref="SignInProviderOrigin.Require"/>).
/// </summary>
public sealed record TrustSetArgs(bool AcceptCertificateException, string? ProviderOrigin = null)
{
   /// <summary>The sign-in provider's origin that the check reported and the operator was shown, or null to state that the check reported none. It names an origin whenever it is given, and cannot be set once the arguments exist. It is compared, never stored.</summary>
   public string? ProviderOrigin { get; } = SignInProviderOrigin.Require(ProviderOrigin, nameof(ProviderOrigin));



   /// <summary>
   /// True when the request may be carried out as far as the sign-in provider goes: it switches the exception off,
   /// which accepts nothing, or what it says the operator was shown is what the service read from the server
   /// (<see cref="SignInProviderOrigin.Agrees"/>). When it is false the service changes nothing and answers
   /// <see cref="ManagementErrorCode.ProviderChanged"/>.
   /// </summary>
   public bool ProviderAgrees(string? readByTheService) => !AcceptCertificateException || SignInProviderOrigin.Agrees(ProviderOrigin, readByTheService);
}
