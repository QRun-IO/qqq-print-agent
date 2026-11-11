/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>
/// How the identity provider's half of a sign-out ended (see <see cref="ProviderSignOut"/>). A page that shows the
/// result has to tell these apart, because what the operator is told to do differs: nothing, look at the browser
/// setting, look at the page that is still open, or nothing because it was stopped on purpose. The log has the
/// kind of failure behind the middle two.
/// </summary>
public enum ProviderSignOutResult
{
   /// <summary>The provider's page was opened and the browser came back to this application's registered callback with this sign-out's own state: the provider's session has ended.</summary>
   Confirmed,

   /// <summary>The provider's page was not opened, so the operator saw nothing and the provider's session is as it was: the default browser could not be started, or the callback inbox could not be opened, in which case no browser was started either.</summary>
   NotOpened,

   /// <summary>The provider's page was opened and the provider did not confirm: the browser did not come back within the limit, it came back with this sign-out's state on another address, or the callback inbox failed while the sign-out waited. The page may still be open, and the provider's session may or may not have ended.</summary>
   NotConfirmed,

   /// <summary>The caller's token ended the sign-out before the provider confirmed. When the token had already ended as the sign-out was started, nothing was opened; otherwise the provider's page was opened and may still be open.</summary>
   Cancelled,
}
