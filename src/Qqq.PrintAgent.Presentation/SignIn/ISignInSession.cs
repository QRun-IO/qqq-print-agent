/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>
/// One sign-in attempt against one server, as the sign-in flow needs it: discover what the server offers, sign in by
/// password or through the browser, and export the resulting session credential for the service. The companion's
/// implementation wraps the real session client; a test supplies a scripted one. Disposing it abandons the attempt.
/// </summary>
public interface ISignInSession : IDisposable
{
   /// <summary>The server this attempt belongs to.</summary>
   Uri Origin { get; }



   /// <summary>Checks that the server speaks a compatible API and returns how it authenticates users.</summary>
   Task<QqqAuthenticationMetadata> DiscoverAsync(CancellationToken cancellationToken);



   /// <summary>Signs in with a user name and password, which are used for this request only and never kept.</summary>
   Task SignInWithPasswordAsync(string username, string password, CancellationToken cancellationToken);



   /// <summary>Starts a browser sign-in for the given redirect address and returns the provider page to open.</summary>
   Task<Uri> BeginBrowserSignInAsync(Uri redirect, CancellationToken cancellationToken);



   /// <summary>Finishes the browser sign-in that was begun on this session, from the callback address the browser was sent to.</summary>
   Task CompleteBrowserSignInAsync(Uri callback, CancellationToken cancellationToken);



   /// <summary>Returns the session credential, the only secret handed to the service; it fails when no sign-in completed.</summary>
   QqqSessionCredential ExportCredential();



   /// <summary>
   /// The certificate this session refused most recently, with the origin that presented it, or null when it has
   /// refused none. The session runs as the signed-in Windows user, whose certificate store can differ from the
   /// service's, and it talks to its server and to the sign-in provider that server names, so this is how a caller
   /// learns that a step failed over a certificate and which of the two presented it. It is public certificate data.
   /// </summary>
   QqqCertificateRejection? LastCertificateRejection { get; }
}
