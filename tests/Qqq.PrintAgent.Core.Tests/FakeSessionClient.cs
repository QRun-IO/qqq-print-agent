/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Text;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// A hand-written session for tests of code that sends printing requests through one. It implements only what the
/// interface requires, so the interface's own default members (provider sign-out, the realtime socket) are the ones a
/// test observes. Printing requests are recorded and answered from a queue, or, when the test has a fake server of its
/// own, passed on to it under the printing extension's path, as the real session passes them to the real server.
/// </summary>
internal sealed class FakeSessionClient(HttpMessageHandler? server = null) : IQqqSessionClient
{
   private readonly Queue<Func<HttpResponseMessage>> _responses = new();
   private readonly HttpMessageInvoker? _server = server is null ? null : new HttpMessageInvoker(server, disposeHandler: false);



   /// <summary>The API client as the product builds it, through a session, with the session's requests answered by the test's own server. The clock is the one the client counts its limits on.</summary>
   internal static PrintApiClient ApiOver(HttpMessageHandler server, TimeProvider? timeProvider = null) => PrintApiClient.ForSession(new FakeSessionClient(server), timeProvider);



   /// <summary>Every printing request received: method, relative path with query, and body.</summary>
   internal List<(HttpMethod Method, string Path, string? Body)> Printing { get; } = [];



   /// <summary>How many times the session was signed out.</summary>
   internal int SignOuts { get; private set; }



   /// <inheritdoc/>
   public Uri Origin { get; } = new("https://printing.example.invalid/");



   /// <inheritdoc/>
   public bool IsAuthenticated => true;



   /// <summary>Queues the answer to the next printing request.</summary>
   internal FakeSessionClient Answer(string body, HttpStatusCode status = HttpStatusCode.OK)
   {
      _responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
      return this;
   }



   /// <summary>Records the request, then passes it to the test's server when there is one and otherwise returns the next queued answer.</summary>
   public async Task<HttpResponseMessage> SendPrintingAsync(HttpMethod method, string relativePath, HttpContent? content = null, CancellationToken cancellationToken = default)
   {
      Printing.Add((method, relativePath, content is null ? null : await content.ReadAsStringAsync(cancellationToken)));
      if(_server is not null)
      {
         using var request = new HttpRequestMessage(method, new Uri(Origin, PrintingSessionHandler.Prefix + relativePath)) { Content = content };
         return await _server.SendAsync(request, cancellationToken);
      }
      Assert.True(_responses.Count > 0, "The client sent an unscripted printing request: " + relativePath);
      return _responses.Dequeue()();
   }



   /// <summary>Counts the sign-out.</summary>
   public Task SignOutAsync(CancellationToken cancellationToken = default)
   {
      SignOuts++;
      return Task.CompletedTask;
   }



   /// <inheritdoc/>
   public Task<QqqApiDiscovery> DiscoverAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();



   /// <inheritdoc/>
   public Task<QqqAuthenticationMetadata> GetAuthenticationMetadataAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();



   /// <inheritdoc/>
   public Task SignInWithPasswordAsync(string username, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();



   /// <inheritdoc/>
   public Task<QqqBrowserLogin> BeginBrowserLoginAsync(Uri redirectUri, CancellationToken cancellationToken = default) => throw new NotSupportedException();



   /// <inheritdoc/>
   public Task CompleteBrowserLoginAsync(QqqBrowserLogin attempt, Uri callback, CancellationToken cancellationToken = default) => throw new NotSupportedException();



   /// <inheritdoc/>
   public Task RestoreSessionAsync(QqqSessionCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();



   /// <inheritdoc/>
   public Task ResumeSessionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();



   /// <inheritdoc/>
   public QqqSessionCredential ExportCredential() => throw new NotSupportedException();



   /// <inheritdoc/>
   public Task<QqqSessionBinding> GetSessionIdentityAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();



   /// <inheritdoc/>
   public Task<QqqSessionBinding> BindDeviceAsync(QqqDeviceCredential credential, bool claimLegacyDevice = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();



   /// <summary>Releases the invoker in front of the test's server; the server itself belongs to the test.</summary>
   public void Dispose() => _server?.Dispose();
}
