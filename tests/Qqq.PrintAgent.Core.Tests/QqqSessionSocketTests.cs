/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// The session client decides everything about its transports: no redirects, no platform cookie jar, one logged
/// certificate policy, what the realtime upgrade carries and what each upgrade outcome means. These tests watch those
/// decisions through a fake transport, so none of them needs a platform socket.
/// </summary>
public sealed class QqqSessionSocketTests
{
   private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);
   private static readonly TimeSpan HeartbeatLimit = TimeSpan.FromSeconds(60);



   /// <summary>
   /// The upgrade goes to the realtime route on the session's own origin (wss for https, ws for loopback http), offers the
   /// printing subprotocol, and carries the session cookie, the origin, the client channel, the device proof and the
   /// negotiated keep-alive. The connected socket is handed over and the attempt is not abandoned.
   /// </summary>
   [Theory]
   [InlineData("https://printing.example.invalid", "wss://printing.example.invalid/qqq/v1/qbit/printing/device/realtime")]
   [InlineData("http://127.0.0.1:18581", "ws://127.0.0.1:18581/qqq/v1/qbit/printing/device/realtime")]
   public async Task UpgradeCarriesTheSessionAndDeviceProofToTheSessionOrigin(string origin, string expected)
   {
      (QqqSessionClient client, FakeTransport transport, ScriptedServer server, _) = await BoundClientAsync(new Uri(origin));
      using(client)
      {
         using WebSocket socket = await client.ConnectPrintingAsync(Heartbeat, HeartbeatLimit);
         FakeSocketConnector connector = Assert.Single(transport.Connectors);
         QqqSocketRequest request = connector.Request!;
         Assert.Equal(new Uri(expected), request.Uri);
         Assert.Equal("qqq-printing.v1", request.SubProtocol);
         var headers = request.Headers.ToDictionary(header => header.Key, header => header.Value, StringComparer.Ordinal);
         Assert.Contains("sessionUUID=session", headers["Cookie"], StringComparison.Ordinal);
         Assert.Equal(origin, headers["Origin"]);
         Assert.Equal(QqqSessionClient.ClientChannel, headers["X-QQQ-Client"]);
         Assert.Equal("device-1", headers["X-QQQ-Device-Id"]);
         Assert.Equal("synthetic-proof", headers["X-QQQ-Device-Token"]);
         Assert.Equal(5, headers.Count);
         Assert.Equal(Heartbeat, request.KeepAliveInterval);
         Assert.Equal(HeartbeatLimit, request.KeepAliveTimeout);
         Assert.Equal("QQQ realtime socket request (redacted)", request.ToString());
         Assert.Same(connector.Socket, socket);
         Assert.True(connector.Released);
         Assert.False(connector.Disposed);
         Assert.Equal("/qqq/v1/version", server.Requests[^1].Uri.AbsolutePath);
         Assert.True(server.Requests[^1].IsPublic);
      }
   }



   /// <summary>Neither the HTTP handler nor the socket may follow a redirect or keep its own cookies: a redirect could carry the session elsewhere, and the cookie is attached by hand.</summary>
   [Fact]
   public async Task TransportsNeverFollowRedirectsOrKeepCookies()
   {
      (QqqSessionClient client, FakeTransport transport, _, _) = await BoundClientAsync();
      using(client)
      {
         using WebSocket socket = await client.ConnectPrintingAsync(Heartbeat, HeartbeatLimit);
         Assert.Equal(new QqqTransportPolicy(FollowRedirects: false, UseCookieJar: false), transport.HttpPolicy);
         Assert.Equal(new QqqTransportPolicy(FollowRedirects: false, UseCookieJar: false), transport.SocketPolicy);
      }
   }



   /// <summary>A server that answers the upgrade with 401 or 403 has withdrawn authorization: the failure says sign-in is required, so the runtime stops reconnecting, and the attempt is abandoned.</summary>
   [Theory]
   [InlineData(HttpStatusCode.Unauthorized)]
   [InlineData(HttpStatusCode.Forbidden)]
   public async Task RefusedUpgradeMeansAuthorizationIsRequired(HttpStatusCode status)
   {
      (QqqSessionClient client, FakeTransport transport, _, List<string> info) = await BoundClientAsync();
      using(client)
      {
         transport.Prepare = connector =>
         {
            connector.UpgradeStatus = status;
            connector.Connecting = _ => throw new WebSocketException(WebSocketError.NotAWebSocket, "The server returned status code '" + (int)status + "' when status code '101' was expected.");
         };
         QqqSessionException failure = await Assert.ThrowsAsync<QqqSessionException>(() => client.ConnectPrintingAsync(Heartbeat, HeartbeatLimit));
         Assert.Equal(QqqSessionException.FailureKind.AuthenticationRequired, failure.Failure);
         Assert.Contains($"Realtime socket upgrade refused with HTTP {(int)status} (NotAWebSocket); authorization is required.", info, StringComparer.Ordinal);
         Assert.True(transport.Connectors[0].Disposed);
         Assert.False(transport.Connectors[0].Released);
      }
   }



   /// <summary>
   /// Any other failure of the upgrade reaches the caller unchanged, so the runtime retries, and the log says what it
   /// was: the WebSocket error with the HTTP status when there was one, a cancellation, or the failure's own description.
   /// </summary>
   [Theory]
   [InlineData("bad-gateway", "Realtime socket upgrade failed: NotAWebSocket, HTTP 502: Synthetic upgrade failure.")]
   [InlineData("no-answer", "Realtime socket upgrade failed: Faulted: Synthetic upgrade failure.")]
   [InlineData("cancelled", "Realtime socket connection was cancelled or timed out.")]
   [InlineData("dropped", "Realtime socket connection failed: IOException: Synthetic connection loss.")]
   public async Task OtherUpgradeFailuresReachTheCallerAndAreExplainedInTheLog(string fault, string line)
   {
      (QqqSessionClient client, FakeTransport transport, _, List<string> info) = await BoundClientAsync();
      using(client)
      {
         Exception thrown = fault switch
         {
            "bad-gateway" => new WebSocketException(WebSocketError.NotAWebSocket, "Synthetic upgrade failure."),
            "no-answer" => new WebSocketException(WebSocketError.Faulted, "Synthetic upgrade failure."),
            "cancelled" => new OperationCanceledException("Synthetic handshake timeout."),
            _ => new IOException("Synthetic connection loss."),
         };
         transport.Prepare = connector =>
         {
            connector.UpgradeStatus = string.Equals(fault, "bad-gateway", StringComparison.Ordinal) ? HttpStatusCode.BadGateway : default;
            connector.Connecting = _ => throw thrown;
         };
         Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectPrintingAsync(Heartbeat, HeartbeatLimit));
         Assert.Same(thrown, failure);
         Assert.Contains(line, info, StringComparer.Ordinal);
         Assert.True(transport.Connectors[0].Disposed);
         Assert.True(client.IsAuthenticated);
      }
   }



   /// <summary>A server that upgrades but does not select the printing subprotocol is not speaking this agent's protocol: the socket is refused as an invalid response and abandoned.</summary>
   [Theory]
   [InlineData(null)]
   [InlineData("qqq-printing.v2")]
   public async Task UpgradeWithoutThePrintingSubprotocolIsRefused(string? selected)
   {
      (QqqSessionClient client, FakeTransport transport, _, List<string> info) = await BoundClientAsync();
      using(client)
      {
         transport.Prepare = connector => connector.SubProtocol = selected;
         QqqSessionException failure = await Assert.ThrowsAsync<QqqSessionException>(() => client.ConnectPrintingAsync(Heartbeat, HeartbeatLimit));
         Assert.Equal(QqqSessionException.FailureKind.InvalidResponse, failure.Failure);
         Assert.Contains("Realtime socket refused: the server did not select the qqq-printing.v1 subprotocol.", info, StringComparer.Ordinal);
         Assert.True(transport.Connectors[0].Disposed);
         Assert.False(transport.Connectors[0].Released);
      }
   }



   /// <summary>If the user signs out while the socket is connecting, the socket that arrives belongs to a session that no longer exists: it is discarded and sign-in is required.</summary>
   [Fact]
   public async Task SocketThatConnectsAfterSignOutIsDiscarded()
   {
      (QqqSessionClient client, FakeTransport transport, ScriptedServer server, List<string> info) = await BoundClientAsync();
      using(client)
      {
         server.Add("{}");
         transport.Prepare = connector => connector.Connecting = token => client.SignOutAsync(token);
         QqqSessionException failure = await Assert.ThrowsAsync<QqqSessionException>(() => client.ConnectPrintingAsync(Heartbeat, HeartbeatLimit));
         Assert.Equal(QqqSessionException.FailureKind.AuthenticationRequired, failure.Failure);
         Assert.Contains("Realtime socket discarded: the session changed while it was connecting.", info, StringComparer.Ordinal);
         Assert.True(transport.Connectors[0].Disposed);
         Assert.False(client.IsAuthenticated);
      }
   }



   /// <summary>Without a session, or with a session that has no approved device, no upgrade is attempted at all, so the cookie and the device proof are never offered.</summary>
   [Theory]
   [InlineData(false, QqqSessionException.FailureKind.AuthenticationRequired)]
   [InlineData(true, QqqSessionException.FailureKind.PermissionDenied)]
   public async Task NoUpgradeIsAttemptedWithoutASessionAndAnApprovedDevice(bool signedIn, QqqSessionException.FailureKind expected)
   {
      ScriptedServer server = ScriptedServer.SignedIn().Add(ScriptedServer.Discovery);
      var transport = new FakeTransport(server);
      using var client = new QqqSessionClient(ScriptedServer.Origin, null, null, false, null, null, false, transport);
      if(signedIn)
      { await client.SignInWithPasswordAsync("synthetic-user", "synthetic-password"); }
      QqqSessionException failure = await Assert.ThrowsAsync<QqqSessionException>(() => client.ConnectPrintingAsync(Heartbeat, HeartbeatLimit));
      Assert.Equal(expected, failure.Failure);
      FakeSocketConnector connector = Assert.Single(transport.Connectors);
      Assert.Null(connector.Request);
      Assert.True(connector.Disposed);
   }



   /// <summary>A discovery document with an empty slot in its extension list, which a hostile or broken server can send, is handled as data: the one real printing extension is still found and the socket still opens.</summary>
   [Fact]
   public async Task EmptySlotInTheExtensionListDoesNotBreakTheUpgrade()
   {
      (QqqSessionClient client, FakeTransport transport, _, _) = await BoundClientAsync(discovery: ScriptedServer.Discovery.Replace("\"extensions\":[", "\"extensions\":[null,", StringComparison.Ordinal));
      using(client)
      {
         using WebSocket socket = await client.ConnectPrintingAsync(Heartbeat, HeartbeatLimit);
         Assert.True(transport.Connectors[0].Released);
      }
   }



   /// <summary>
   /// Both transports get this client's one certificate policy. For HTTP the rejection is logged against the host the
   /// request is going to (or the origin when the request has no address); for the socket against the origin, whichever
   /// certificate type the platform hands over, and a missing certificate is refused rather than dereferenced.
   /// </summary>
   [Fact]
   public async Task BothTransportsUseTheLoggedCertificatePolicy()
   {
      (QqqSessionClient client, FakeTransport transport, _, List<string> info) = await BoundClientAsync();
      using(client)
      {
         using WebSocket socket = await client.ConnectPrintingAsync(Heartbeat, HeartbeatLimit);
         using X509Certificate2 certificate = SelfSigned("CN=Synthetic Authority");
         using var legacy = new X509Certificate(certificate.Handle);
         using var addressed = new HttpRequestMessage(HttpMethod.Get, "https://other.example.invalid:8443/qqq/v1/version");
         using var unaddressed = new HttpRequestMessage();

         Assert.False(transport.HttpCertificate!(addressed, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
         Assert.Contains(info, line => line.StartsWith("TLS certificate for other.example.invalid:8443 rejected: RemoteCertificateChainErrors.", StringComparison.Ordinal));
         Assert.False(transport.HttpCertificate(unaddressed, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));
         Assert.Contains(info, line => line.StartsWith("TLS certificate for printing.example.invalid rejected: RemoteCertificateNameMismatch.", StringComparison.Ordinal) && line.Contains("not issued for this host name", StringComparison.Ordinal));
         Assert.True(transport.HttpCertificate(addressed, certificate, null, SslPolicyErrors.None));

         info.Clear();
         Assert.False(transport.SocketCertificate!(this, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
         Assert.False(transport.SocketCertificate(this, legacy, null, SslPolicyErrors.RemoteCertificateChainErrors));
         Assert.Equal(2, info.Count(line => line.StartsWith("TLS certificate for printing.example.invalid rejected: RemoteCertificateChainErrors.", StringComparison.Ordinal) && line.Contains("CN=Synthetic Authority", StringComparison.Ordinal)));
         Assert.False(transport.SocketCertificate(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
         Assert.Contains(info, line => line.Contains("no certificate presented", StringComparison.Ordinal) && line.EndsWith("The server presented no usable certificate.", StringComparison.Ordinal));
         Assert.True(transport.SocketCertificate(this, certificate, null, SslPolicyErrors.None));
      }
   }



   /// <summary>A client over the fake transport that has signed in with a password and bound an approved device, with the discovery its next socket attempt will read already queued.</summary>
   private static async Task<(QqqSessionClient Client, FakeTransport Transport, ScriptedServer Server, List<string> Info)> BoundClientAsync(Uri? origin = null, string discovery = ScriptedServer.Discovery)
   {
      var info = new List<string>();
      ScriptedServer server = ScriptedServer.SignedIn().Add(ScriptedServer.Identity).Add(discovery);
      var transport = new FakeTransport(server);
      var client = new QqqSessionClient(origin ?? ScriptedServer.Origin, null, null, allowLoopbackHttp: true, null, new QqqSessionDiagnostics(info.Add), false, transport);
      await client.SignInWithPasswordAsync("synthetic-user", "synthetic-password");
      await client.BindDeviceAsync(new QqqDeviceCredential("device-1", "synthetic-proof"));
      return (client, transport, server, info);
   }



   /// <summary>A throwaway self-signed certificate for the policy tests; it is never installed anywhere.</summary>
   private static X509Certificate2 SelfSigned(string subject)
   {
      using var key = ECDsa.Create();
      var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
      return request.CreateSelfSigned(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
   }
}
