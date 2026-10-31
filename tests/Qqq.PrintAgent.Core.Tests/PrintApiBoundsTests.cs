using System.Net;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

public sealed class PrintApiBoundsTests
{
   [Fact]
   public async Task Claim_RejectsOversizedControlResponse()
   {
      using ResponseHandler handler = new(() => new ByteArrayContent(new byte[2 * 1024 * 1024 + 1]));
      using PrintApiClient api = new("https://synthetic.invalid", handler: handler);
      await Assert.ThrowsAsync<HttpRequestException>(() => api.ClaimAsync(CancellationToken.None));
   }

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

   private sealed class ResponseHandler(Func<HttpContent> content) : HttpMessageHandler
   {
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
         => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content() });
   }
}
