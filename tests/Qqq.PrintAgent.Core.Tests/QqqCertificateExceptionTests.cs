/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// The certificate exception is a set of at most two origins, the server's own and the sign-in provider it names, and
/// the session client accepts an untrusted certificate only for a request that is going to one of them. These tests
/// hold the value to its limits (two origins, well-formed HTTPS origins only, never widened) and the client to the
/// rule for HTTP requests: accepted from the server's origin, from the provider's only when it is named, and refused
/// from every other host whatever the exception holds. The socket upgrade is held to the same rule in
/// QqqSessionSocketTests.
/// </summary>
public sealed class QqqCertificateExceptionTests
{
   private static readonly Uri Server = new("https://dev.example.invalid:8443");
   private static readonly Uri Provider = new("https://id.example.invalid");



   /// <summary>No exception covers nothing, says so, and is what building from no origins gives.</summary>
   [Fact]
   public void NoExceptionCoversNothing()
   {
      Assert.True(QqqCertificateExceptionOrigins.None.IsEmpty);
      Assert.Empty(QqqCertificateExceptionOrigins.None.Origins);
      Assert.Equal("none", QqqCertificateExceptionOrigins.None.ToString());
      Assert.False(QqqCertificateExceptionOrigins.None.Covers(Server));
      Assert.Same(QqqCertificateExceptionOrigins.None, QqqCertificateExceptionOrigins.For());
   }



   /// <summary>An exception holds one or two origins in one form (scheme, lowercase host, port, the path "/"), counts an origin given twice once, and names them in the order given.</summary>
   [Fact]
   public void ExceptionHoldsOneOrTwoNormalizedOrigins()
   {
      var one = QqqCertificateExceptionOrigins.For(new Uri("https://DEV.Example.Invalid:8443"));
      Assert.False(one.IsEmpty);
      Assert.Equal(["https://dev.example.invalid:8443/"], one.Origins.Select(origin => origin.AbsoluteUri), StringComparer.Ordinal);

      var two = QqqCertificateExceptionOrigins.For(Server, new Uri("https://id.example.invalid/"), new Uri("https://dev.example.invalid:8443/"));
      Assert.Equal(["https://dev.example.invalid:8443/", "https://id.example.invalid/"], two.Origins.Select(origin => origin.AbsoluteUri), StringComparer.Ordinal);
      Assert.Equal("https://dev.example.invalid:8443, https://id.example.invalid", two.ToString());
      Assert.Equal(2, QqqCertificateExceptionOrigins.MaximumOrigins);
   }



   /// <summary>A third distinct origin cannot be added: an exception wider than the server and its sign-in provider cannot be built.</summary>
   [Fact]
   public void MoreThanTwoOriginsCannotBeBuilt()
   {
      ArgumentException refused = Assert.Throws<ArgumentException>(() => QqqCertificateExceptionOrigins.For(Server, Provider, new Uri("https://third.example.invalid")));
      Assert.StartsWith("A certificate exception covers at most two origins: the server's own and the sign-in provider it names.", refused.Message, StringComparison.Ordinal);
      Assert.Equal("origins", refused.ParamName);
   }



   /// <summary>
   /// Only a well-formed HTTPS origin can be named: plain HTTP (which presents no certificate), another scheme, user
   /// information, a path, a query, a fragment, a relative address and a missing one are each refused, and so is a
   /// missing list.
   /// </summary>
   [Theory]
   [InlineData("http://dev.example.invalid:8443")]
   [InlineData("wss://dev.example.invalid:8443")]
   [InlineData("https://operator@dev.example.invalid:8443")]
   [InlineData("https://dev.example.invalid:8443/qqq")]
   [InlineData("https://dev.example.invalid:8443/?next=1")]
   [InlineData("https://dev.example.invalid:8443/#top")]
   [InlineData("/qqq/v1")]
   [InlineData(null)]
   public void MalformedOriginCannotBeNamed(string? address)
   {
      Uri? origin = address is null ? null : new Uri(address, UriKind.RelativeOrAbsolute);
      ArgumentException refused = Assert.Throws<ArgumentException>(() => QqqCertificateExceptionOrigins.For(Server, origin!));
      Assert.StartsWith("A certificate exception names HTTPS origins only: an absolute https address with no user information, path, query or fragment.", refused.Message, StringComparison.Ordinal);
      Assert.Throws<ArgumentNullException>(() => QqqCertificateExceptionOrigins.For(null!));
   }



   /// <summary>
   /// An address is covered when it is on a named origin: the same host and port over HTTPS or its WebSocket form,
   /// whatever its path, and without regard to the letter case of the host. Another port, another host (one that only
   /// starts with a covered name included), plain HTTP or plain WebSocket, and a relative address are not.
   /// </summary>
   [Theory]
   [InlineData("https://dev.example.invalid:8443/qqq/v1/version?x=1", true)]
   [InlineData("wss://dev.example.invalid:8443/qqq/v1/qbit/printing/device/realtime", true)]
   [InlineData("https://DEV.EXAMPLE.INVALID:8443/", true)]
   [InlineData("https://id.example.invalid:443/.well-known/openid-configuration", true)]
   [InlineData("https://dev.example.invalid/", false)]
   [InlineData("https://dev.example.invalid:8444/", false)]
   [InlineData("https://other.example.invalid:8443/", false)]
   [InlineData("https://id.example.invalid.attacker.invalid/", false)]
   [InlineData("http://dev.example.invalid:8443/", false)]
   [InlineData("ws://dev.example.invalid:8443/", false)]
   [InlineData("/qqq/v1/version", false)]
   public void AddressIsCoveredOnlyOnANamedOrigin(string address, bool covered)
   {
      var exception = QqqCertificateExceptionOrigins.For(Server, Provider);
      Assert.Equal(covered, exception.Covers(new Uri(address, UriKind.RelativeOrAbsolute)));
      Assert.Throws<ArgumentNullException>(() => exception.Covers(null!));
   }



   /// <summary>
   /// The exception an operator accepted for a server is built from the server's address as the caller holds it: only
   /// its origin is used, a server on plain HTTP contributes nothing because it presents no certificate, and the
   /// provider's origin is added when it is known. A provider on the server's own origin adds nothing.
   /// </summary>
   [Theory]
   [InlineData("https://dev.example.invalid:8443/some/path?x=1", null, "https://dev.example.invalid:8443/")]
   [InlineData("https://operator@dev.example.invalid:8443", "https://id.example.invalid", "https://dev.example.invalid:8443/ https://id.example.invalid/")]
   [InlineData("https://dev.example.invalid:8443", "https://dev.example.invalid:8443", "https://dev.example.invalid:8443/")]
   [InlineData("http://127.0.0.1:8080", null, "")]
   [InlineData("http://127.0.0.1:8080", "https://id.example.invalid", "https://id.example.invalid/")]
   [InlineData("/not/an/origin", null, "")]
   public void ExceptionForAServerUsesItsOriginAndTheProviderWhenKnown(string server, string? provider, string origins)
   {
      var exception = QqqCertificateExceptionOrigins.ForServer(new Uri(server, UriKind.RelativeOrAbsolute), provider is null ? null : new Uri(provider));
      Assert.Equal(origins, string.Join(' ', exception.Origins.Select(origin => origin.AbsoluteUri)));
      Assert.Throws<ArgumentNullException>(() => QqqCertificateExceptionOrigins.ForServer(null!));
      Assert.Throws<ArgumentException>(() => QqqCertificateExceptionOrigins.ForServer(Server, new Uri("http://id.example.invalid")));
   }



   /// <summary>
   /// The rule for HTTP requests. A certificate Windows does not trust is accepted when the request is going to the
   /// server's origin and the exception names it; when it is going to the sign-in provider only if the exception names
   /// the provider too; and never when it is going anywhere else, or when there is no exception. An acceptance is
   /// logged as an exception for that origin; a refusal is logged with the certificate, and says which origins the
   /// exception in force covers, so the log shows that the exception did not apply.
   /// </summary>
   [Theory]
   [InlineData("server", "https://dev.example.invalid:8443/qqq/v1/version", true)]
   [InlineData("server", "https://id.example.invalid/.well-known/openid-configuration", false)]
   [InlineData("server+provider", "https://id.example.invalid/.well-known/openid-configuration", true)]
   [InlineData("server+provider", "https://other.example.invalid/qqq/v1/version", false)]
   [InlineData("provider", "https://dev.example.invalid:8443/qqq/v1/version", false)]
   [InlineData("none", "https://dev.example.invalid:8443/qqq/v1/version", false)]
   public void UntrustedCertificateIsAcceptedOnlyForARequestToANamedOrigin(string named, string requested, bool accepted)
   {
      QqqCertificateExceptionOrigins exception = named switch
      {
         "server" => QqqCertificateExceptionOrigins.For(Server),
         "server+provider" => QqqCertificateExceptionOrigins.For(Server, Provider),
         "provider" => QqqCertificateExceptionOrigins.For(Provider),
         _ => QqqCertificateExceptionOrigins.None,
      };
      var info = new List<string>();
      using var scripted = new ScriptedServer();
      var transport = new FakeTransport(scripted);
      using X509Certificate2 certificate = SelfSigned("CN=Synthetic Local Authority");
      using var client = new QqqSessionClient(Server, null, null, false, null, new QqqSessionDiagnostics(info.Add), exception, transport);
      using var request = new HttpRequestMessage(HttpMethod.Get, requested);
      string host = request.RequestUri!.Authority;

      Assert.Same(exception, client.CertificateException);
      Assert.Equal(accepted, transport.HttpCertificate!(request, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
      if(accepted)
      {
         Assert.Contains(info, line => line.StartsWith($"TLS certificate for {host} is NOT trusted by Windows (RemoteCertificateChainErrors) but was accepted because the certificate exception covers this origin:", StringComparison.Ordinal));
         Assert.DoesNotContain(info, line => line.Contains("rejected", StringComparison.Ordinal));
      }
      else
      {
         string rejection = Assert.Single(info, line => line.StartsWith($"TLS certificate for {host} rejected: RemoteCertificateChainErrors.", StringComparison.Ordinal));
         Assert.Contains("CN=Synthetic Local Authority", rejection, StringComparison.Ordinal);
         Assert.Equal(!exception.IsEmpty, rejection.EndsWith($" The certificate exception in force covers {exception} and not this host.", StringComparison.Ordinal));
         Assert.DoesNotContain(info, line => line.Contains("but was accepted", StringComparison.Ordinal));
      }
      Assert.True(transport.HttpCertificate(request, certificate, null, SslPolicyErrors.None));
   }



   /// <summary>A request that carries no address is judged for the client's own origin, so the exception for the server applies to it and an exception for another origin does not.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public void RequestWithoutAnAddressIsJudgedForTheServersOrigin(bool serverNamed)
   {
      using var scripted = new ScriptedServer();
      var transport = new FakeTransport(scripted);
      using X509Certificate2 certificate = SelfSigned("CN=Synthetic Local Authority");
      using var client = new QqqSessionClient(Server, null, null, false, null, null, QqqCertificateExceptionOrigins.For(serverNamed ? Server : Provider), transport);
      using var unaddressed = new HttpRequestMessage();
      Assert.Equal(serverNamed, transport.HttpCertificate!(unaddressed, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
   }



   /// <summary>
   /// A client says at creation what it will accept: nothing untrusted without an exception, and with one, a warning
   /// that names the covered origins and says no other host is covered.
   /// </summary>
   [Fact]
   public void ClientSaysAtCreationWhichOriginsTheExceptionCovers()
   {
      var info = new List<string>();
      using(new QqqSessionClient(Server, diagnostics: new QqqSessionDiagnostics(info.Add)))
      {
         Assert.Equal("Session client created for https://dev.example.invalid:8443 (plain HTTP not allowed; untrusted server certificates refused).", Assert.Single(info));
      }
      info.Clear();
      using(new QqqSessionClient(Server, diagnostics: new QqqSessionDiagnostics(info.Add), certificateException: QqqCertificateExceptionOrigins.For(Server, Provider)))
      {
         Assert.Equal(
         [
            "Session client created for https://dev.example.invalid:8443 (plain HTTP not allowed; untrusted server certificates ALLOWED from https://dev.example.invalid:8443, https://id.example.invalid).",
            "WARNING: certificate validation is relaxed for https://dev.example.invalid:8443, https://id.example.invalid and for no other host. Any certificate presented from there, including a forged one, will be accepted. Use this only for development servers.",
         ], info);
      }
   }



   /// <summary>A throwaway self-signed certificate; it is never installed anywhere.</summary>
   private static X509Certificate2 SelfSigned(string subject)
   {
      using var key = RSA.Create(2048);
      var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
      return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
   }
}
