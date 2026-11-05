/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// The operator printer catalog is the agent's only view of server-side approval. These tests pin the wire shape (the route
/// under the printing extension, the page size, the cursor continuation), the refusal reasons for a server without the catalog
/// or a user without access, the requirement for a signed-in session, and the page bound.
/// </summary>
public sealed class OperatorPrinterCatalogTests
{
   /// <summary>Pages are followed through the opaque cursor until the server returns none, every entry is kept, and unknown members are ignored.</summary>
   [Fact]
   public async Task Catalog_FollowsCursorsAndCollectsEveryPage()
   {
      using CatalogSession session = new(
         """{"printers":[{"printerRowId":"r1","deviceId":"d1","printerId":"p1","name":"Laser","deviceName":"Desk","status":"READY","formats":["PDF"],"mediaProfiles":[]}],"nextCursor":"c1"}""",
         """{"printers":[{"printerRowId":"r2","deviceId":"d2","printerId":"p2","name":"Zebra","deviceName":"Dock","status":"OFFLINE","formats":["ZPL","PDF"],"extra":true}],"nextCursor":null}""");
      using var api = PrintApiClient.ForSession(session);
      IReadOnlyList<OperatorPrinter> printers = await api.OperatorPrintersAsync(CancellationToken.None);
      Assert.Equal(new[] { "p1", "p2" }, printers.Select(printer => printer.PrinterId), StringComparer.Ordinal);
      Assert.Equal(new[] { "d1", "d2" }, printers.Select(printer => printer.DeviceId), StringComparer.Ordinal);
      Assert.Equal(new[] { "ZPL", "PDF" }, printers[1].Formats, StringComparer.Ordinal);
      Assert.Equal(new[] { "operator/printers?limit=200", "operator/printers?limit=200&after=c1" }, session.Paths, StringComparer.Ordinal);
   }



   /// <summary>A 404 is an older server and a 401 or 403 a user without access; each becomes a distinct, explained exception rather than a generic HTTP failure.</summary>
   [Theory]
   [InlineData(HttpStatusCode.NotFound, typeof(NotSupportedException))]
   [InlineData(HttpStatusCode.Forbidden, typeof(UnauthorizedAccessException))]
   [InlineData(HttpStatusCode.Unauthorized, typeof(UnauthorizedAccessException))]
   public async Task Catalog_ExplainsMissingRouteAndRefusedAccess(HttpStatusCode status, Type exception)
   {
      using CatalogSession session = new(status);
      using var api = PrintApiClient.ForSession(session);
      await Assert.ThrowsAsync(exception, () => api.OperatorPrintersAsync(CancellationToken.None));
   }



   /// <summary>The catalog is user-scoped: a client without a signed-in session refuses before any request.</summary>
   [Fact]
   public async Task Catalog_RequiresTheSignedInSession()
   {
      using PrintApiClient api = new("https://synthetic.invalid", "device-token");
      await Assert.ThrowsAsync<InvalidOperationException>(() => api.OperatorPrintersAsync(CancellationToken.None));
   }



   /// <summary>A server that keeps returning cursors is cut off after the page bound instead of being followed forever.</summary>
   [Fact]
   public async Task Catalog_StopsAfterThePageBound()
   {
      using CatalogSession session = new(Enumerable.Repeat("""{"printers":[],"nextCursor":"again"}""", PrintApiClient.CatalogPageLimit + 1).ToArray());
      using var api = PrintApiClient.ForSession(session);
      await Assert.ThrowsAsync<InvalidDataException>(() => api.OperatorPrintersAsync(CancellationToken.None));
      Assert.Equal(PrintApiClient.CatalogPageLimit, session.Paths.Count);
   }



   /// <summary>A session stub that answers the printing routes with scripted pages (or one status) and records the relative paths it was asked for.</summary>
   private sealed class CatalogSession : IQqqSessionClient
   {
      private readonly Queue<string> _pages;
      private readonly HttpStatusCode _status;

      internal CatalogSession(params string[] pages)
      {
         _pages = new Queue<string>(pages);
         _status = HttpStatusCode.OK;
      }

      internal CatalogSession(HttpStatusCode status)
      {
         _pages = new Queue<string>();
         _status = status;
      }

      internal List<string> Paths { get; } = [];
      public Uri Origin { get; } = new("https://synthetic.invalid");
      public bool IsAuthenticated => true;
      public Task<QqqApiDiscovery> DiscoverAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
      public Task<QqqAuthenticationMetadata> GetAuthenticationMetadataAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
      public Task SignInWithPasswordAsync(string username, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
      public Task<QqqBrowserLogin> BeginBrowserLoginAsync(Uri redirectUri, CancellationToken cancellationToken = default) => throw new NotSupportedException();
      public Task CompleteBrowserLoginAsync(QqqBrowserLogin attempt, Uri callback, CancellationToken cancellationToken = default) => throw new NotSupportedException();
      public Task RestoreSessionAsync(QqqSessionCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
      public Task ResumeSessionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
      public Task SignOutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
      public QqqSessionCredential ExportCredential() => throw new NotSupportedException();
      public Task<QqqSessionBinding> GetSessionIdentityAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
      public Task<QqqSessionBinding> BindDeviceAsync(QqqDeviceCredential credential, bool claimLegacyDevice = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();

      public Task<HttpResponseMessage> SendPrintingAsync(HttpMethod method, string relativePath, HttpContent? content = null, CancellationToken cancellationToken = default)
      {
         Paths.Add(relativePath);
         Assert.Equal(HttpMethod.Get, method);
         if(_status != HttpStatusCode.OK)
         { return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent("{}") }); }
         return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_pages.Dequeue()) });
      }

      public void Dispose() { }
   }
}
