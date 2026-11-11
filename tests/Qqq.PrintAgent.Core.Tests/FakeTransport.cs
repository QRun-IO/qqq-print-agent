/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// A hand-written stand-in for the platform transport. It records the policy and certificate callbacks the session
/// client hands over, serves HTTP through a scripted server, and gives each realtime socket attempt a connector whose
/// outcome the test decides. No platform socket or handler is created.
/// </summary>
internal sealed class FakeTransport(HttpMessageHandler http) : IQqqTransport
{
   /// <summary>The policy the client asked the HTTP handler to follow.</summary>
   internal QqqTransportPolicy? HttpPolicy { get; private set; }



   /// <summary>The policy the client asked the most recent socket connector to follow.</summary>
   internal QqqTransportPolicy? SocketPolicy { get; private set; }



   /// <summary>The certificate decision the client supplied for HTTP requests.</summary>
   internal Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>? HttpCertificate { get; private set; }



   /// <summary>The certificate decision the client supplied for the socket upgrade.</summary>
   internal RemoteCertificateValidationCallback? SocketCertificate { get; private set; }



   /// <summary>Every connector handed out, in order.</summary>
   internal List<FakeSocketConnector> Connectors { get; } = [];



   /// <summary>Configures each new connector before the client uses it.</summary>
   internal Action<FakeSocketConnector>? Prepare { get; set; }



   /// <summary>Records the policy and callback and returns the scripted HTTP handler.</summary>
   public HttpMessageHandler CreateHttpHandler(QqqTransportPolicy policy, Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool> certificate)
   {
      HttpPolicy = policy;
      HttpCertificate = certificate;
      return http;
   }



   /// <summary>Records the policy and callback and returns a new fake connector, prepared by the test's hook.</summary>
   public IQqqSocketConnector CreateSocketConnector(QqqTransportPolicy policy, RemoteCertificateValidationCallback certificate)
   {
      SocketPolicy = policy;
      SocketCertificate = certificate;
      var connector = new FakeSocketConnector();
      Prepare?.Invoke(connector);
      Connectors.Add(connector);
      return connector;
   }
}



/// <summary>One fake socket attempt: it keeps the request the client built and reports whatever upgrade outcome the test set.</summary>
internal sealed class FakeSocketConnector : IQqqSocketConnector
{
   /// <summary>The upgrade request the client asked for, or null when it never tried to connect.</summary>
   internal QqqSocketRequest? Request { get; private set; }



   /// <summary>Runs during the upgrade; a test throws from it to fail the upgrade, or changes the session to simulate a race.</summary>
   internal Func<CancellationToken, Task>? Connecting { get; set; }



   /// <summary>The socket handed to the client when the upgrade is accepted.</summary>
   internal ScriptedWebSocket Socket { get; } = new();



   /// <summary>True once the client abandoned the attempt.</summary>
   internal bool Disposed { get; private set; }



   /// <summary>True once the client took the connected socket.</summary>
   internal bool Released { get; private set; }



   /// <summary>The HTTP status the fake server answered the upgrade with; zero means no HTTP answer.</summary>
   public HttpStatusCode UpgradeStatus { get; set; }



   /// <summary>The subprotocol the fake server selected.</summary>
   public string? SubProtocol { get; set; } = "qqq-printing.v1";



   /// <summary>The headers the fake server answers the upgrade with, accepted or refused; null, the default, means there was no HTTP answer.</summary>
   internal Dictionary<string, IEnumerable<string>>? UpgradeHeaders { get; set; }



   /// <summary>How many times the client took the upgrade answer's headers.</summary>
   internal int HeadersTaken { get; private set; }



   /// <summary>Hands the answer's headers over once and keeps none, as the platform connector does.</summary>
   public IReadOnlyDictionary<string, IEnumerable<string>>? TakeUpgradeHeaders()
   {
      HeadersTaken++;
      Dictionary<string, IEnumerable<string>>? headers = UpgradeHeaders;
      UpgradeHeaders = null;
      return headers;
   }



   /// <summary>Keeps the request and runs the test's hook.</summary>
   public async Task ConnectAsync(QqqSocketRequest request, CancellationToken cancellationToken)
   {
      Request = request;
      if(Connecting is not null)
      { await Connecting(cancellationToken); }
   }



   /// <summary>Hands over the scripted socket.</summary>
   public WebSocket Release()
   {
      Released = true;
      return Socket;
   }



   /// <summary>Marks the attempt abandoned.</summary>
   public void Dispose() => Disposed = true;
}
