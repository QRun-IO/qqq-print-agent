/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// The one place the session client obtains the platform's HTTP and WebSocket stacks. The client decides everything
/// (redirects, cookies, certificate policy, the upgrade request) and passes those decisions in; an implementation only
/// constructs platform objects with them. That keeps every decision in code a test can run, with the production
/// implementation reduced to construction.
/// </summary>
internal interface IQqqTransport
{
   /// <summary>Creates the handler behind the client's HTTP requests, applying the given policy and asking the given callback about every server certificate.</summary>
   HttpMessageHandler CreateHttpHandler(QqqTransportPolicy policy, Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool> certificate);



   /// <summary>Creates a connector for one realtime socket attempt, with the same policy and its own certificate callback.</summary>
   IQqqSocketConnector CreateSocketConnector(QqqTransportPolicy policy, RemoteCertificateValidationCallback certificate);
}
