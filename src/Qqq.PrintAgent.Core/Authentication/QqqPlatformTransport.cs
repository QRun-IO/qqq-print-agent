/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// The production transport: it constructs the platform's HTTP handler and WebSocket connector from the policy and
/// certificate callbacks the session client hands it, and decides nothing itself.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Constructs HttpClientHandler and the platform socket connector, which only do anything against a live TLS endpoint. Every setting applied here is chosen by QqqSessionClient and asserted through a fake IQqqTransport.")]
internal sealed class QqqPlatformTransport : IQqqTransport
{
   /// <summary>The single instance; the transport holds no state.</summary>
   internal static QqqPlatformTransport Instance { get; } = new();



   /// <summary>Builds the platform HTTP handler with the client's redirect and cookie policy and its certificate callback.</summary>
   public HttpMessageHandler CreateHttpHandler(QqqTransportPolicy policy, Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool> certificate)
   {
      // MA0039: the callback is QqqSessionClient's, which keeps the platform's verdict (it accepts only SslPolicyErrors.None)
      // unless the administrator explicitly allowed untrusted certificates for a development server; it exists so a
      // rejection is logged with its reason instead of failing silently.
#pragma warning disable MA0039
      return new HttpClientHandler
      {
         AllowAutoRedirect = policy.FollowRedirects,
         UseCookies = policy.UseCookieJar,
         ServerCertificateCustomValidationCallback = certificate,
      };
#pragma warning restore MA0039
   }



   /// <summary>Builds a connector for one realtime socket attempt.</summary>
   public IQqqSocketConnector CreateSocketConnector(QqqTransportPolicy policy, RemoteCertificateValidationCallback certificate) => new QqqPlatformSocketConnector(policy, certificate);
}
