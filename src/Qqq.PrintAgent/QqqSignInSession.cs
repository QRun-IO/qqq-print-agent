/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.SignIn;

namespace Qqq.PrintAgent;

/// <summary>
/// The sign-in flow's session over the real QQQ session client. It adds nothing of its own: it forwards each step and
/// remembers the one browser attempt in progress, which only the session client can create and complete.
/// </summary>
internal sealed class QqqSignInSession(QqqSessionClient client) : ISignInSession
{
   private QqqBrowserLogin? _attempt;



   /// <summary>The server this session belongs to.</summary>
   public Uri Origin => client.Origin;



   /// <summary>Checks the server's API discovery, then returns how it authenticates users.</summary>
   public async Task<QqqAuthenticationMetadata> DiscoverAsync(CancellationToken cancellationToken)
   {
      _ = await client.DiscoverAsync(cancellationToken);
      return await client.GetAuthenticationMetadataAsync(cancellationToken);
   }



   /// <summary>Signs in with a user name and password.</summary>
   public Task SignInWithPasswordAsync(string username, string password, CancellationToken cancellationToken)
      => client.SignInWithPasswordAsync(username, password, cancellationToken);



   /// <summary>Begins the browser sign-in, keeps the attempt, and returns the provider page to open.</summary>
   public async Task<Uri> BeginBrowserSignInAsync(Uri redirect, CancellationToken cancellationToken)
   {
      _attempt = await client.BeginBrowserLoginAsync(redirect, cancellationToken);
      return _attempt.AuthorizationUri;
   }



   /// <summary>Completes the attempt that was begun on this session with the callback the browser was sent to.</summary>
   public Task CompleteBrowserSignInAsync(Uri callback, CancellationToken cancellationToken)
      => client.CompleteBrowserLoginAsync(_attempt ?? throw new InvalidOperationException("No browser sign-in was begun on this session."), callback, cancellationToken);



   /// <summary>Returns the session credential that is handed to the service.</summary>
   public QqqSessionCredential ExportCredential() => client.ExportCredential();



   /// <summary>The certificate the session client refused most recently, with the origin that presented it; null when it refused none.</summary>
   public QqqCertificateRejection? LastCertificateRejection => client.LastCertificateRejection;



   /// <summary>Releases the session client.</summary>
   public void Dispose() => client.Dispose();
}
