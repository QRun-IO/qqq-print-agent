/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// Sends an API client's requests through the signed-in session, which attaches the cookie and device proof and refuses
/// anything outside the native printing routes, so the API client itself holds no credential. The session is not owned
/// here and is never disposed by this handler.
/// </summary>
internal sealed class PrintingSessionHandler(IQqqSessionClient session) : HttpMessageHandler
{
   /// <summary>The path every native printing route lives under.</summary>
   internal const string Prefix = "/qqq/v1/qbit/printing/";



   /// <summary>Accepts only requests under the printing extension path, strips that prefix and hands the rest, with its query, to the session; any other address throws before a request is made.</summary>
   protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
   {
      string path = request.RequestUri!.AbsolutePath;
      if(!path.StartsWith(Prefix, StringComparison.Ordinal))
      {
         throw new InvalidOperationException("Request is outside the native printing extension.");
      }
      return session.SendPrintingAsync(request.Method, path[Prefix.Length..] + request.RequestUri.Query, request.Content, cancellationToken);
   }
}
