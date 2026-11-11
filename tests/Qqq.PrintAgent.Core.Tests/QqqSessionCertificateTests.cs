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
/// Certificate policy of the session client: a certificate Windows does not trust is refused by default with a logged reason and
/// remedy, accepted only from an origin the certificate exception names (still logged, as a warning naming the certificate), and a
/// trusted one is accepted with its identity at debug level only. Failures are never silent.
/// </summary>
public sealed class QqqSessionCertificateTests
{
   private static readonly Uri Origin = new("https://127.0.0.1:18581");



   /// <summary>A self-signed certificate with chain errors is refused and the log names the error, the issuer, the LocalSystem store caveat and both remedies.</summary>
   [Fact]
   public void UntrustedCertificateIsRefusedByDefaultWithRemedy()
   {
      var info = new List<string>();
      using X509Certificate2 certificate = SelfSigned("CN=Caddy Local Authority - Test");
      using var client = new QqqSessionClient(Origin, diagnostics: new QqqSessionDiagnostics(info.Add));
      Assert.True(client.CertificateException.IsEmpty);
      Assert.False(client.EvaluateCertificate(new Uri("https://127.0.0.1:18581"), certificate, SslPolicyErrors.RemoteCertificateChainErrors));
      string rejection = Assert.Single(info, line => line.StartsWith("TLS certificate for 127.0.0.1:18581 rejected: RemoteCertificateChainErrors", StringComparison.Ordinal));
      Assert.Contains("Caddy Local Authority - Test", rejection, StringComparison.Ordinal);
      Assert.Contains("LocalSystem", rejection, StringComparison.Ordinal);
      Assert.Contains("Local Machine Trusted Root", rejection, StringComparison.Ordinal);
      Assert.Contains("Allow untrusted server certificate", rejection, StringComparison.Ordinal);
   }



   /// <summary>With a certificate exception for the server's origin, the same certificate from that origin is accepted and the acceptance is logged as an exception, never silently.</summary>
   [Fact]
   public void UntrustedCertificateIsAcceptedOnlyWhenAllowedAndLoudly()
   {
      var info = new List<string>();
      using X509Certificate2 certificate = SelfSigned("CN=dev-server");
      using var client = new QqqSessionClient(Origin, diagnostics: new QqqSessionDiagnostics(info.Add), certificateException: QqqCertificateExceptionOrigins.For(Origin));
      Assert.Equal(["https://127.0.0.1:18581/"], client.CertificateException.Origins.Select(origin => origin.AbsoluteUri), StringComparer.Ordinal);
      Assert.Contains(info, line => line.StartsWith("WARNING: certificate validation is relaxed", StringComparison.Ordinal));
      Assert.True(client.EvaluateCertificate(new Uri("https://127.0.0.1:18581"), certificate, SslPolicyErrors.RemoteCertificateChainErrors));
      Assert.Contains(info, line => line.Contains("NOT trusted by Windows (RemoteCertificateChainErrors) but was accepted", StringComparison.Ordinal) && line.Contains("dev-server", StringComparison.Ordinal));
   }



   /// <summary>A certificate without policy errors is accepted; its identity is recorded only at debug level, so a healthy production server does not fill the log.</summary>
   [Fact]
   public void TrustedCertificateIsAcceptedQuietly()
   {
      var info = new List<string>();
      var debug = new List<string>();
      using X509Certificate2 certificate = SelfSigned("CN=print.example.invalid");
      using var client = new QqqSessionClient(new Uri("https://print.example.invalid"), diagnostics: new QqqSessionDiagnostics(info.Add, debug.Add));
      Assert.True(client.EvaluateCertificate(new Uri("https://print.example.invalid"), certificate, SslPolicyErrors.None));
      Assert.DoesNotContain(info, line => line.Contains("TLS certificate", StringComparison.Ordinal));
      Assert.Contains(debug, line => line.StartsWith("TLS certificate for print.example.invalid accepted by Windows", StringComparison.Ordinal));
   }



   /// <summary>Describe and Explain walk inner exceptions, so the real cause behind "see inner exception" reaches the log and the screen.</summary>
   [Fact]
   public void FailureDescriptionsIncludeInnerCauses()
   {
      var failure = new HttpRequestException("The SSL connection could not be established, see inner exception.",
         new System.Security.Authentication.AuthenticationException("The remote certificate is invalid according to the validation procedure: RemoteCertificateChainErrors"));
      Assert.Equal("HttpRequestException: The SSL connection could not be established, see inner exception. <- AuthenticationException: The remote certificate is invalid according to the validation procedure: RemoteCertificateChainErrors",
         QqqSessionDiagnostics.Describe(failure));
      Assert.Equal("The SSL connection could not be established, see inner exception. The remote certificate is invalid according to the validation procedure: RemoteCertificateChainErrors",
         QqqSessionDiagnostics.Explain(failure));
   }



   /// <summary>A throwaway self-signed certificate for the policy tests; it is never installed anywhere.</summary>
   private static X509Certificate2 SelfSigned(string subject)
   {
      using var key = ECDsa.Create();
      var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
      return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
   }
}
