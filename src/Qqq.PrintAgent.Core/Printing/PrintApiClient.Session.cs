/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// Native session part of the API client: routes every request through the signed-in QQQ session instead of a device bearer token, and
/// confines those requests to the printing extension's path.
/// </summary>
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



   /// <summary>Sends this client's requests through the session, which attaches the cookie and device proof and refuses anything outside the native printing path, so the client itself holds no credential.</summary>
   private sealed class SessionHandler(IQqqSessionClient session) : HttpMessageHandler
   {
      /// <summary>Accepts only requests under the printing extension path, strips that prefix and hands the rest to the session; any other address throws before a request is made.</summary>
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
