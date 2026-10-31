/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>UI and service seam for standard QQQ sessions. Implementations do not persist passwords or provider tokens.</summary>
public interface IQqqSessionClient : IDisposable
{
   Uri Origin { get; }
   bool IsAuthenticated { get; }
   Task<QqqApiDiscovery> DiscoverAsync(CancellationToken cancellationToken = default);
   Task<QqqAuthenticationMetadata> GetAuthenticationMetadataAsync(CancellationToken cancellationToken = default);
   Task SignInWithPasswordAsync(string username, string password, CancellationToken cancellationToken = default);
   Task<QqqBrowserLogin> BeginBrowserLoginAsync(Uri redirectUri, CancellationToken cancellationToken = default);
   Task CompleteBrowserLoginAsync(QqqBrowserLogin attempt, Uri callback, CancellationToken cancellationToken = default);
   Task RestoreSessionAsync(QqqSessionCredential credential, CancellationToken cancellationToken = default);
   Task ResumeSessionAsync(CancellationToken cancellationToken = default);
   Task SignOutAsync(CancellationToken cancellationToken = default);
   QqqSessionCredential ExportCredential();
   Task<QqqSessionBinding> GetSessionIdentityAsync(CancellationToken cancellationToken = default);
   Task<QqqSessionBinding> BindDeviceAsync(QqqDeviceCredential credential, bool claimLegacyDevice = false, CancellationToken cancellationToken = default);
   Task<HttpResponseMessage> SendPrintingAsync(HttpMethod method, string relativePath, HttpContent? content = null, CancellationToken cancellationToken = default);
}
