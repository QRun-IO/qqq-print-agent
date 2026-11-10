/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Security;
using System.Net.WebSockets;

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// The production socket connector: a <see cref="ClientWebSocket"/> and the HTTP invoker that carries its upgrade. It
/// copies the session client's request onto the socket options and connects; what the outcome means is decided by the
/// client.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "A thin adapter over the platform network stack: it constructs ClientWebSocket and SocketsHttpHandler and copies a prepared request onto them, and decides nothing. The request, the policy, the certificate decision and the handling of every outcome live in QqqSessionClient and are tested through a fake IQqqSocketConnector. One loopback test does run this class end to end, against a peer inside the test process and over plain WebSocket, which shows the wiring works; it is still excluded because that is one accepted upgrade, and what else the class can meet (a TLS handshake, a refused upgrade, an abandoned attempt) is the platform's behavior against a real endpoint, not logic here to cover.")]
internal sealed class QqqPlatformSocketConnector : IQqqSocketConnector
{
   private readonly ClientWebSocket _socket = new();
   private readonly HttpMessageInvoker _invoker;



   /// <summary>Creates the socket and the invoker for its upgrade with the client's redirect and cookie policy and its certificate callback.</summary>
   internal QqqPlatformSocketConnector(QqqTransportPolicy policy, RemoteCertificateValidationCallback certificate)
   {
      // MA0039: see QqqPlatformTransport.CreateHttpHandler; the callback is the session client's logged certificate policy.
#pragma warning disable MA0039
      _invoker = new HttpMessageInvoker(new SocketsHttpHandler
      {
         AllowAutoRedirect = policy.FollowRedirects,
         UseCookies = policy.UseCookieJar,
         SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = certificate },
      });
#pragma warning restore MA0039
   }



   /// <summary>The HTTP status of the upgrade response, collected by the socket.</summary>
   public HttpStatusCode UpgradeStatus => _socket.HttpStatusCode;



   /// <summary>The subprotocol the server selected.</summary>
   public string? SubProtocol => _socket.SubProtocol;



   /// <summary>Copies the request onto the socket options and connects through the invoker.</summary>
   public Task ConnectAsync(QqqSocketRequest request, CancellationToken cancellationToken)
   {
      _socket.Options.AddSubProtocol(request.SubProtocol);
      _socket.Options.CollectHttpResponseDetails = true;
      foreach(KeyValuePair<string, string> header in request.Headers)
      { _socket.Options.SetRequestHeader(header.Key, header.Value); }
      _socket.Options.KeepAliveInterval = request.KeepAliveInterval;
      _socket.Options.KeepAliveTimeout = request.KeepAliveTimeout;
      return _socket.ConnectAsync(request.Uri, _invoker, cancellationToken);
   }



   /// <summary>Wraps the connected socket so that disposing it also disposes the invoker that carried its upgrade.</summary>
   public WebSocket Release() => new OwnedPrintingSocket(_socket, _invoker);



   /// <summary>Abandons the attempt: disposes the socket and its invoker.</summary>
   public void Dispose()
   {
      _socket.Dispose();
      _invoker.Dispose();
   }
}
