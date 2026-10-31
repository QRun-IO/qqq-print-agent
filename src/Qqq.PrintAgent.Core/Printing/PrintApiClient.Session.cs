using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Core.Printing;

public sealed partial class PrintApiClient
{
   private bool _nativeSession;
   private IQqqSessionClient? _nativeSessionClient;

   /// <summary>The session remains owned by the service; disposing an API request adapter does not sign it out.</summary>
   public static PrintApiClient ForSession(IQqqSessionClient session)
   {
      PrintApiClient client = new(session.Origin.AbsoluteUri, handler: new SessionHandler(session));
      client._nativeSession = true;
      client._nativeSessionClient = session;
      client._http.BaseAddress = new Uri(session.Origin, "/qqq/v1/qbit/printing/");
      return client;
   }

   private sealed class SessionHandler(IQqqSessionClient session) : HttpMessageHandler
   {
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         const string prefix = "/qqq/v1/qbit/printing/";
         string path = request.RequestUri!.AbsolutePath;
         if(!path.StartsWith(prefix, StringComparison.Ordinal))
         {
            throw new InvalidOperationException("Request is outside the native printing extension.");
         }
         return session.SendPrintingAsync(request.Method, path[prefix.Length..] + request.RequestUri.Query, request.Content, cancellationToken);
      }
   }
}
