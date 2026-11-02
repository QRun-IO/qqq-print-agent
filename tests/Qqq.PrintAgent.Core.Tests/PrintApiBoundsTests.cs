/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>Size limits in the print API client: small control replies are capped so a bad server cannot exhaust memory, while document downloads, which are legitimately large, are not held to that cap.</summary>
public sealed class PrintApiBoundsTests
{
   /// <summary>A claim reply just over 2 MiB is rejected as a request failure, bounding what a server can make the client buffer for a control message.</summary>
   [Fact]
   public async Task Claim_RejectsOversizedControlResponse()
   {
      using ResponseHandler handler = new(() => new ByteArrayContent(new byte[2 * 1024 * 1024 + 1]));
      using PrintApiClient api = new("https://synthetic.invalid", handler: handler);
      await Assert.ThrowsAsync<HttpRequestException>(() => api.ClaimAsync(CancellationToken.None));
   }



   /// <summary>A 3 MiB document, larger than the control-reply cap, downloads and matches its hash, showing the control limit does not apply to document content.</summary>
   [Fact]
   public async Task Download_AllowsDocumentLargerThanControlResponseLimit()
   {
      byte[] document = new byte[3 * 1024 * 1024];
      string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(document));
      using ResponseHandler handler = new(() => new ByteArrayContent(document));
      using PrintApiClient api = new("https://synthetic.invalid", handler: handler);
      PrintJob job = new("job", "printer", "Title", "PDF", hash, document.Length, new PrintOptions(), "", "CLAIMED");
      Assert.Equal(document, await api.DownloadAsync(job, CancellationToken.None));
   }



   /// <summary>Fake server that answers every request with whatever content the supplied function produces.</summary>
   private sealed class ResponseHandler(Func<HttpContent> content) : HttpMessageHandler
   {
      /// <summary>Returns 200 with freshly built content for each request.</summary>
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
         => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content() });
   }
}
