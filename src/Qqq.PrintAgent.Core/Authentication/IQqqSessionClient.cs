/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>UI and service seam for standard QQQ sessions. Implementations do not persist passwords or provider tokens.</summary>
public interface IQqqSessionClient : IDisposable
{
   /// <summary>The one server origin this session belongs to; every request is confined to it.</summary>
   Uri Origin { get; }

   /// <summary>True while a session token is held. It says nothing about whether the server still accepts that token.</summary>
   bool IsAuthenticated { get; }



   /// <summary>Returns the server's discovery document: API version, features and the printing extension this agent negotiates against.</summary>
   Task<QqqApiDiscovery> DiscoverAsync(CancellationToken cancellationToken = default);



   /// <summary>Returns how the server authenticates users, so the caller can offer password sign-in or the browser flow that matches.</summary>
   Task<QqqAuthenticationMetadata> GetAuthenticationMetadataAsync(CancellationToken cancellationToken = default);



   /// <summary>Signs in with a table-based user name and password, which are used for that request only and never kept.</summary>
   Task SignInWithPasswordAsync(string username, string password, CancellationToken cancellationToken = default);



   /// <summary>Starts the browser sign-in for a redirect address the caller will receive, returning the attempt to complete later.</summary>
   Task<QqqBrowserLogin> BeginBrowserLoginAsync(Uri redirectUri, CancellationToken cancellationToken = default);



   /// <summary>Finishes a browser sign-in from the callback address the provider redirected to, checked against the attempt that began it.</summary>
   Task CompleteBrowserLoginAsync(QqqBrowserLogin attempt, Uri callback, CancellationToken cancellationToken = default);



   /// <summary>Re-adopts a saved session credential after a restart, after the server confirms it still accepts it.</summary>
   Task RestoreSessionAsync(QqqSessionCredential credential, CancellationToken cancellationToken = default);



   /// <summary>Renews the held session so the user is not asked to sign in again; the credential may change and must be saved again.</summary>
   Task ResumeSessionAsync(CancellationToken cancellationToken = default);



   /// <summary>Ends the session on the server where possible and always forgets it locally.</summary>
   Task SignOutAsync(CancellationToken cancellationToken = default);



   /// <summary>Returns the credential that lets a later start restore this session; it is the only secret the caller persists.</summary>
   QqqSessionCredential ExportCredential();



   /// <summary>Asks the server who the session belongs to, so a changed user, tenant or device can be detected and refused.</summary>
   Task<QqqSessionBinding> GetSessionIdentityAsync(CancellationToken cancellationToken = default);



   /// <summary>
   /// Binds the paired device to this session and returns the confirmed identity. A legacy device can be claimed by this
   /// call when the caller explicitly asks for it; otherwise an existing binding is only verified.
   /// </summary>
   Task<QqqSessionBinding> BindDeviceAsync(QqqDeviceCredential credential, bool claimLegacyDevice = false, CancellationToken cancellationToken = default);



   /// <summary>Sends a request to a native printing route under the session, and returns the response for the caller to dispose; other routes are refused.</summary>
   Task<HttpResponseMessage> SendPrintingAsync(HttpMethod method, string relativePath, HttpContent? content = null, CancellationToken cancellationToken = default);



   /// <summary>
   /// Opens the authenticated realtime WebSocket. The default throws, so a transport that cannot carry realtime printing
   /// fails loudly instead of leaving the service silently unable to claim work.
   /// </summary>
   Task<System.Net.WebSockets.WebSocket> ConnectPrintingAsync(TimeSpan heartbeatInterval, TimeSpan heartbeatTimeout, CancellationToken cancellationToken = default)
      => throw new NotSupportedException("This session transport does not support realtime printing.");
}
