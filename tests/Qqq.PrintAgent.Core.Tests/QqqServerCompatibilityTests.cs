/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// The compatibility check is what the connection wizard shows the operator: each way a server can be unusable must come
/// back as its own typed problem, with the earlier checks recognisably passed, without a credential ever being sent, and
/// without changing what <c>DiscoverAsync</c> has always thrown. "No credential is sent" can only be shown by a client
/// that has one, so the tests that claim it run the check on a client that is signed in and has a device bound, and
/// then prove the session and the binding are exactly as they were.
/// </summary>
public sealed class QqqServerCompatibilityTests
{
   private const int SignInRequests = 4;
   private static readonly Uri Redirect = new(ScriptedServer.Callback);



   /// <summary>
   /// A table-based server with printing 2.x is compatible: no problems, no failed stage, a password sign-in on offer.
   /// The client is signed in with a device bound when it checks, and the check makes exactly the two public requests,
   /// neither carrying the session cookie or the device proof, and leaves the session as it was.
   /// </summary>
   [Fact]
   public async Task TableBasedServerIsCompatibleAndOffersPassword()
   {
      var info = new List<string>();
      using ScriptedServer server = BoundServer().Add(ScriptedServer.Discovery).Add(ScriptedServer.Table);
      using QqqSessionClient client = await BoundClientAsync(server, new QqqSessionDiagnostics(info.Add));
      QqqServerCompatibility result = await client.CheckCompatibilityAsync();
      Assert.True(result.IsCompatible);
      Assert.Empty(result.Problems);
      Assert.Null(result.FailedStage);
      Assert.Null(result.Certificate);
      Assert.Equal(QqqSignInMethods.Password, result.SignInMethods);
      Assert.Equal("2.4.0", result.Discovery!.PrintingExtensionVersion);
      Assert.Contains("Compatibility check passed: printing extension 2.4.0; sign-in by Password.", info, StringComparer.Ordinal);
      await AssertCheckWasPublicAndLeftTheSessionAloneAsync(client, server, "/qqq/v1/version", "/qqq/v1/metaData/authentication");
   }



   /// <summary>A failure to get any answer (name not found, connection refused, a dropped connection, or no answer within the limit) is "unreachable" at the connection stage, each with its own explanation, and no discovery is reported.</summary>
   [Theory]
   [InlineData("name", "the server name could not be found")]
   [InlineData("refused", "the server refused the connection or is not listening on that port")]
   [InlineData("dropped", "the connection failed before the server answered")]
   [InlineData("timeout", "the server did not answer in time")]
   public async Task NoAnswerIsReportedAsUnreachable(string fault, string detail)
   {
      using var server = new ScriptedServer();
      server.AddFailure(() => fault switch
      {
         "name" => new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known."),
         "refused" => new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made."),
         "dropped" => new IOException("The response ended prematurely."),
         _ => new TaskCanceledException("The request timed out."),
      });
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      QqqServerCompatibility result = await client.CheckCompatibilityAsync();
      QqqCompatibilityProblem problem = Assert.Single(result.Problems);
      Assert.Equal(QqqCompatibilityIssue.ServerUnreachable, problem.Issue);
      Assert.Equal(detail, problem.Detail);
      Assert.Equal(QqqCompatibilityStage.Connection, result.FailedStage);
      Assert.False(result.IsCompatible);
      Assert.Null(result.Discovery);
      Assert.Null(result.Certificate);
      Assert.Equal(QqqSignInMethods.None, result.SignInMethods);
   }



   /// <summary>
   /// A certificate the client refused is reported at the security stage together with the certificate itself (issuer,
   /// validity, thumbprint and the policy errors), so the wizard can show what an exception would trust. Each kind of
   /// refusal has its own explanation.
   /// </summary>
   [Theory]
   [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, "Windows does not trust the authority that issued the server's certificate")]
   [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch, "the server's certificate was issued for a different name or address")]
   [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable, "the server presented no usable certificate")]
   public async Task RefusedCertificateIsReportedWithTheCertificate(SslPolicyErrors errors, string detail)
   {
      using X509Certificate2 certificate = SelfSigned("CN=Synthetic Local Authority");
      bool presented = errors != SslPolicyErrors.RemoteCertificateNotAvailable;
      using var server = new ScriptedServer();
      QqqSessionClient? client = null;
      server.AddAsync(_ =>
      {
         // The platform asks the client about the certificate during the handshake and fails the request when it says no.
         Assert.False(client!.EvaluateCertificate(new Uri("https://printing.example.invalid"), presented ? certificate : null, errors));
         return Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established.",
            new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback.")));
      });
      using(client = new QqqSessionClient(ScriptedServer.Origin, server))
      {
         QqqServerCompatibility result = await client.CheckCompatibilityAsync();
         QqqCompatibilityProblem problem = Assert.Single(result.Problems);
         Assert.Equal(QqqCompatibilityIssue.SecureConnectionFailed, problem.Issue);
         Assert.Equal(detail, problem.Detail);
         Assert.Equal(QqqCompatibilityStage.Security, result.FailedStage);
         QqqCertificateRejection rejection = Assert.IsType<QqqCertificateRejection>(result.Certificate);
         Assert.Equal(errors, rejection.Errors);
         Assert.Equal(errors == SslPolicyErrors.RemoteCertificateChainErrors, rejection.IsUntrustedAuthority);
         Assert.Equal(errors == SslPolicyErrors.RemoteCertificateNameMismatch, rejection.IsNameMismatch);
         Assert.Equal(presented ? certificate.Thumbprint : null, rejection.Thumbprint);
         Assert.Equal(presented ? "CN=Synthetic Local Authority" : null, rejection.Issuer);
         Assert.Equal(presented ? "CN=Synthetic Local Authority" : null, rejection.Subject);
         Assert.Equal(presented ? certificate.NotAfter.ToUniversalTime() : null, rejection.NotAfterUtc);
         Assert.Equal(presented ? certificate.NotBefore.ToUniversalTime() : null, rejection.NotBeforeUtc);
      }
   }



   /// <summary>
   /// An exception for another origin does not let this server's certificate through. With the exception naming only
   /// the sign-in provider, the server's own untrusted certificate is refused, and the check reports the secure
   /// connection failure together with the refused certificate, as it does when there is no exception at all. The
   /// reported certificate says which origin presented it, here the server's, and when, on the client's clock; the
   /// client's own record of its last refusal is the same one, and the next check begins without it.
   /// </summary>
   [Fact]
   public async Task CertificateFromAHostTheExceptionDoesNotNameIsRefusedAndReported()
   {
      var time = new ManualTimeProvider();
      time.Advance(TimeSpan.FromHours(2));
      using X509Certificate2 certificate = SelfSigned("CN=Synthetic Local Authority");
      using var server = new ScriptedServer();
      QqqSessionClient? client = null;
      server.AddAsync(_ =>
      {
         Assert.False(client!.EvaluateCertificate(new Uri(ScriptedServer.Origin, "/qqq/v1/version"), certificate, SslPolicyErrors.RemoteCertificateChainErrors));
         return Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established.",
            new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback.")));
      }).Add(ScriptedServer.Discovery).Add(ScriptedServer.Table);
      using(client = new QqqSessionClient(ScriptedServer.Origin, server, time, certificateException: QqqCertificateExceptionOrigins.For(new Uri("https://id.example.invalid"))))
      {
         QqqServerCompatibility result = await client.CheckCompatibilityAsync();
         Assert.Equal(QqqCompatibilityIssue.SecureConnectionFailed, Assert.Single(result.Problems).Issue);
         QqqCertificateRejection rejection = Assert.IsType<QqqCertificateRejection>(result.Certificate);
         Assert.Equal(certificate.Thumbprint, rejection.Thumbprint);
         Assert.True(rejection.IsUntrustedAuthority);
         Assert.Equal("https://printing.example.invalid/", rejection.Origin?.AbsoluteUri);
         Assert.Equal(new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero), rejection.ObservedAt);
         Assert.Same(rejection, client.LastCertificateRejection);
         Assert.Null(result.ProviderOrigin);

         Assert.True((await client.CheckCompatibilityAsync()).IsCompatible);
         Assert.Null(client.LastCertificateRejection);
      }
   }



   /// <summary>
   /// The check reports the origin of the sign-in provider the server names whenever the sign-in description could be
   /// read and names one: for a usable browser sign-in, and equally when sign-in cannot be offered (here the callback
   /// is not listed), so the operator can be shown both addresses before a certificate exception is saved. A password
   /// server names no provider, a description that could not be read names nothing, and an issuer that is not a plain
   /// HTTPS address is not an origin a certificate could come from. The server writes the issuer, so the origin is
   /// reported with the host's ASCII name: a look-alike letter shows as the "xn--" name it is, a zero-width space is
   /// not there, and an issuer whose host holds a line separator has no ASCII name and is reported as no origin. Such
   /// an issuer is also not one a browser sign-in can use, so the server is not compatible.
   /// </summary>
   [Theory]
   [InlineData("usable", "https://id.example.invalid/")]
   [InlineData("callback-not-listed", "https://id.example.invalid/")]
   [InlineData("password", null)]
   [InlineData("unreadable", null)]
   [InlineData("issuer-not-https", null)]
   [InlineData("issuer-with-user", null)]
   [InlineData("issuer-look-alike", "https://xn--d-evb.example.invalid/")]
   [InlineData("issuer-zero-width", "https://id.example.invalid/")]
   [InlineData("issuer-line-separator", null)]
   public async Task CompatibilityReportsTheOriginOfTheProviderTheServerNames(string description, string? provider)
   {
      using var server = new ScriptedServer();
      server.Add(ScriptedServer.Discovery).Add(description switch
      {
         "password" => ScriptedServer.Table,
         "unreadable" => "<html>sign in</html>",
         "issuer-not-https" => ScriptedServer.OAuth.Replace("\"issuer\":\"https://", "\"issuer\":\"http://", StringComparison.Ordinal),
         "issuer-with-user" => ScriptedServer.OAuth.Replace("\"issuer\":\"https://", "\"issuer\":\"https://operator@", StringComparison.Ordinal),
         "issuer-look-alike" => ScriptedServer.OAuth.Replace("\"issuer\":\"https://id.", "\"issuer\":\"https://\u0456d.", StringComparison.Ordinal),
         "issuer-zero-width" => ScriptedServer.OAuth.Replace("\"issuer\":\"https://id.", "\"issuer\":\"https://id\u200B.", StringComparison.Ordinal),
         "issuer-line-separator" => ScriptedServer.OAuth.Replace("\"issuer\":\"https://id.", "\"issuer\":\"https://id\u2028.", StringComparison.Ordinal),
         _ => ScriptedServer.OAuth,
      });
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      var redirect = new Uri(string.Equals(description, "callback-not-listed", StringComparison.Ordinal) ? "io.qrun.other-app:/oauth/callback" : ScriptedServer.Callback);

      QqqServerCompatibility result = await client.CheckCompatibilityAsync(redirect);

      Assert.Equal(provider, result.ProviderOrigin?.AbsoluteUri);
      Assert.Equal(description is not ("callback-not-listed" or "unreadable" or "issuer-not-https" or "issuer-with-user" or "issuer-line-separator"), result.IsCompatible);
      Assert.NotNull(result.Discovery);
   }



   /// <summary>
   /// A sign-in provider cannot be used when the issuer or the address of its discovery document has a host with no
   /// ASCII name: the platform throws when asked to connect to such a host. The check therefore offers no browser
   /// sign-in for it, names the unusable address in printable form (the line separator shows as a space), and never
   /// asks for the provider's document. A wizard that offered the browser button here would start a sign-in that
   /// ends in the platform's exception instead of a refusal. With a nameable issuer the provider's origin is still
   /// reported, as for any other description that was read.
   /// </summary>
   [Theory]
   [InlineData("issuer", "issuer 'https://id .example.invalid/application/' is not a plain HTTPS address", null)]
   [InlineData("discoveryUrl", "discovery address 'https://id .example.invalid/application/.well-known/openid-configuration' is not a plain HTTPS address", "https://id.example.invalid/")]
   public async Task ProviderAddressWithoutANameableHostOffersNoBrowserSignIn(string member, string detail, string? provider)
   {
      using var server = new ScriptedServer();
      server.Add(ScriptedServer.Discovery).Add(ScriptedServer.OAuth.Replace("\"" + member + "\":\"https://id.", "\"" + member + "\":\"https://id\u2028.", StringComparison.Ordinal));
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);

      QqqServerCompatibility result = await client.CheckCompatibilityAsync(Redirect);

      Assert.False(result.IsCompatible);
      Assert.Equal(QqqSignInMethods.None, result.SignInMethods);
      QqqCompatibilityProblem problem = Assert.Single(result.Problems);
      Assert.Equal(QqqCompatibilityIssue.SignInUnavailable, problem.Issue);
      Assert.Equal(detail, problem.Detail);
      Assert.Equal(provider, result.ProviderOrigin?.AbsoluteUri);
      Assert.Equal(new[] { "/qqq/v1/version", "/qqq/v1/metaData/authentication" }, server.Requests.Select(request => request.Uri.AbsolutePath), StringComparer.Ordinal);
   }



   /// <summary>A TLS failure that no certificate decision caused (a protocol mismatch, say) is still a secure connection failure, in either form the platform reports it, but names no certificate, and a certificate refused during an earlier check is not carried into a later one.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task HandshakeFailureWithoutACertificateDecisionNamesNoCertificate(bool classified)
   {
      using X509Certificate2 certificate = SelfSigned("CN=Earlier Authority");
      using var server = new ScriptedServer();
      server.AddFailure(() => classified
         ? new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established.")
         : new HttpRequestException("The SSL connection could not be established, see inner exception.", new AuthenticationException("Authentication failed.")));
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      Assert.False(client.EvaluateCertificate(new Uri("https://printing.example.invalid"), certificate, SslPolicyErrors.RemoteCertificateChainErrors));
      QqqServerCompatibility result = await client.CheckCompatibilityAsync();
      QqqCompatibilityProblem problem = Assert.Single(result.Problems);
      Assert.Equal(QqqCompatibilityIssue.SecureConnectionFailed, problem.Issue);
      Assert.Equal("the secure connection could not be established (the TLS handshake failed)", problem.Detail);
      Assert.Null(result.Certificate);
   }



   /// <summary>
   /// An address that answers over HTTP but not with QQQ discovery (a web page, an error, a redirect, a demand for
   /// authorization, a JSON array, an oversized body) is "not a QQQ server" at the server stage, after exactly one
   /// public request. The client holds a session and a bound device throughout: none of it is sent, and none of these
   /// answers, not even a 401, takes the session away, because the request it answers carried no credential.
   /// </summary>
   [Theory]
   [InlineData("page", "the discovery answer was not a valid QQQ document")]
   [InlineData("array", "the discovery answer was not a valid QQQ document")]
   [InlineData("oversized", "the discovery answer was not a valid QQQ document")]
   [InlineData("missing", "the server refused or redirected the discovery request")]
   [InlineData("redirect", "the server refused or redirected the discovery request")]
   [InlineData("unauthorized", "the server asks for authorization before answering the discovery request")]
   [InlineData("forbidden", "the server asks for authorization before answering the discovery request")]
   public async Task AnswerThatIsNotDiscoveryIsReportedAsNotAQqqServer(string fault, string detail)
   {
      using ScriptedServer server = BoundServer();
      server.AddResponse(() => fault switch
      {
         "page" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>welcome</html>", Encoding.UTF8, "text/html") },
         "array" => ScriptedServer.Json("[]"),
         "oversized" => ScriptedServer.Json(new string(' ', 65_537)),
         "missing" => ScriptedServer.Json("{}", HttpStatusCode.NotFound),
         "redirect" => ScriptedServer.Json("{}", HttpStatusCode.Redirect),
         "unauthorized" => ScriptedServer.Json("{}", HttpStatusCode.Unauthorized),
         _ => ScriptedServer.Json("{}", HttpStatusCode.Forbidden),
      });
      using QqqSessionClient client = await BoundClientAsync(server);
      QqqServerCompatibility result = await client.CheckCompatibilityAsync();
      QqqCompatibilityProblem problem = Assert.Single(result.Problems);
      Assert.Equal(QqqCompatibilityIssue.NotQqqServer, problem.Issue);
      Assert.StartsWith(detail, problem.Detail, StringComparison.Ordinal);
      Assert.Equal(QqqCompatibilityStage.Server, result.FailedStage);
      Assert.Null(result.Discovery);
      await AssertCheckWasPublicAndLeftTheSessionAloneAsync(client, server, "/qqq/v1/version");
   }



   /// <summary>
   /// A redirect is refused, not followed. The server here answers discovery with a redirect to another host and stands
   /// ready to serve that host as well; it follows its own redirect, as the platform handler would, whenever the policy
   /// the client handed the transport allows it. The client's policy does not, so the other host is never asked and the
   /// check reports the redirect. Were the policy ever relaxed, or the client to follow the address itself, the other
   /// host would be asked and this test would fail; the last lines prove the server really does follow when allowed.
   /// </summary>
   [Fact]
   public async Task RedirectIsRefusedAndItsTargetIsNeverAsked()
   {
      using var server = new RedirectingServer();
      var transport = new FakeTransport(server);
      server.Policy = () => transport.HttpPolicy;
      using var client = new QqqSessionClient(ScriptedServer.Origin, null, null, false, null, null, QqqCertificateExceptionOrigins.None, transport);
      QqqServerCompatibility result = await client.CheckCompatibilityAsync();
      QqqCompatibilityProblem problem = Assert.Single(result.Problems);
      Assert.Equal(QqqCompatibilityIssue.NotQqqServer, problem.Issue);
      Assert.Equal("the server refused or redirected the discovery request", problem.Detail);
      Assert.Equal(new QqqTransportPolicy(FollowRedirects: false, UseCookieJar: false), transport.HttpPolicy);
      Assert.Equal(new Uri(ScriptedServer.Origin, "/qqq/v1/version"), Assert.Single(server.Asked));
      Assert.False(server.TargetAsked);

      server.Policy = () => new QqqTransportPolicy(FollowRedirects: true, UseCookieJar: false);
      using var following = new HttpMessageInvoker(server, disposeHandler: false);
      using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ScriptedServer.Origin, "/qqq/v1/version"));
      using HttpResponseMessage followed = await following.SendAsync(request, CancellationToken.None);
      Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
      Assert.True(server.TargetAsked);
   }



   /// <summary>
   /// Each shortfall of a real QQQ discovery document has its own issue and stage, the document is still returned for
   /// display, and the unsuitable server is not asked anything further. A 1.x printing extension is "too old" here even
   /// though sign-in still accepts it.
   /// </summary>
   [Theory]
   [InlineData("1.10.0", "1.7.0", QqqCompatibilityIssue.ApiVersionUnsupported, QqqCompatibilityStage.Server, "API version '1.7.0' is not a plain 1.8+ release")]
   [InlineData("1.10.0", "1.10.0-rc.1", QqqCompatibilityIssue.ApiVersionUnsupported, QqqCompatibilityStage.Server, "API version '1.10.0-rc.1' is not a plain 1.8+ release")]
   [InlineData("\"widgets\",", "", QqqCompatibilityIssue.ApiFeatureMissing, QqqCompatibilityStage.Server, "missing feature(s) widgets")]
   [InlineData("\"features\":[", "\"other\":[", QqqCompatibilityIssue.ApiFeatureMissing, QqqCompatibilityStage.Server, "missing feature(s) version, metaData, processes, processCancel, extensions, widgets")]
   [InlineData("[\"api\"]", "[\"windows\"]", QqqCompatibilityIssue.ClientChannelMissing, QqqCompatibilityStage.Server, "the 'api' client channel is not offered")]
   [InlineData("\"printing\"", "\"other\"", QqqCompatibilityIssue.PrintingExtensionMissing, QqqCompatibilityStage.Printing, "no printing extension is advertised (is the printing QBit installed and enabled?)")]
   [InlineData("2.4.0", "1.4.0", QqqCompatibilityIssue.PrintingExtensionTooOld, QqqCompatibilityStage.Printing, "printing extension 1.4.0 cannot deliver jobs to this agent; upgrade the server to printing extension 2.x")]
   [InlineData("2.4.0", "3.0.0", QqqCompatibilityIssue.PrintingExtensionUnsupported, QqqCompatibilityStage.Printing, "printing extension version '3.0.0' is not a plain 1.x or 2.x release")]
   [InlineData("/qbit/printing", "/other", QqqCompatibilityIssue.PrintingExtensionUnsupported, QqqCompatibilityStage.Printing, "printing extension path '/other' is not /qbit/printing")]
   [InlineData("}]}", "},{\"name\":\"printing\",\"version\":\"2.4.0\",\"pathPrefix\":\"/qbit/printing\"}]}", QqqCompatibilityIssue.PrintingExtensionUnsupported, QqqCompatibilityStage.Printing, "2 printing extensions are advertised, expected exactly one")]
   public async Task EachDiscoveryShortfallHasItsOwnIssueAndStage(string oldValue, string newValue, QqqCompatibilityIssue issue, QqqCompatibilityStage stage, string detail)
   {
      using var server = new ScriptedServer();
      server.Add(ScriptedServer.Discovery.Replace(oldValue, newValue, StringComparison.Ordinal));
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      QqqServerCompatibility result = await client.CheckCompatibilityAsync();
      QqqCompatibilityProblem problem = Assert.Single(result.Problems);
      Assert.Equal(issue, problem.Issue);
      Assert.Equal(detail, problem.Detail);
      Assert.Equal(stage, problem.Stage);
      Assert.Equal(stage, result.FailedStage);
      Assert.True(result.Has(issue));
      Assert.NotNull(result.Discovery);
      Assert.Equal(QqqSignInMethods.None, result.SignInMethods);
      Assert.Single(server.Requests);
   }



   /// <summary>A document with no features member at all, as a non-QQQ JSON API might return, reports the missing list instead of failing on a null.</summary>
   [Fact]
   public async Task NullCollectionsInDiscoveryAreReportedNotDereferenced()
   {
      using var server = new ScriptedServer();
      server.Add("""{"apiVersion":"1.10.0","features":null,"clientChannels":null,"extensions":null}""");
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      QqqServerCompatibility result = await client.CheckCompatibilityAsync();
      Assert.Equal(new[] { QqqCompatibilityIssue.ApiFeatureMissing, QqqCompatibilityIssue.ClientChannelMissing, QqqCompatibilityIssue.PrintingExtensionMissing }, result.Problems.Select(problem => problem.Issue));
      Assert.Equal("no features list", result.Problems[0].Detail);
      Assert.Null(result.Discovery!.PrintingExtensionVersion);
      Assert.Equal(QqqCompatibilityStage.Server, result.FailedStage);
   }



   /// <summary>Several shortfalls are all reported, and the failed stage is the earliest of them, so the wizard does not send the operator to fix printing before the API version.</summary>
   [Fact]
   public async Task EveryProblemIsReportedAndTheEarliestStageWins()
   {
      var info = new List<string>();
      using var server = new ScriptedServer();
      server.Add(ScriptedServer.Discovery.Replace("\"printing\"", "\"other\"", StringComparison.Ordinal).Replace("1.10.0", "1.7.0", StringComparison.Ordinal));
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: new QqqSessionDiagnostics(info.Add));
      QqqServerCompatibility result = await client.CheckCompatibilityAsync();
      Assert.Equal(new[] { QqqCompatibilityIssue.ApiVersionUnsupported, QqqCompatibilityIssue.PrintingExtensionMissing }, result.Problems.Select(problem => problem.Issue));
      Assert.Equal(QqqCompatibilityStage.Server, result.FailedStage);
      Assert.False(result.Has(QqqCompatibilityIssue.ServerUnreachable));
      Assert.Contains(info, line => line.StartsWith("Compatibility check found 2 problem(s): ApiVersionUnsupported (API version '1.7.0'", StringComparison.Ordinal));
   }



   /// <summary>Text the server controls is cut short and stripped of control characters before it reaches a problem's detail, so a hostile version string cannot forge log lines or flood the wizard.</summary>
   [Fact]
   public async Task ServerSuppliedTextIsBoundedAndPrintable()
   {
      using var server = new ScriptedServer();
      server.Add(ScriptedServer.Discovery.Replace("1.10.0", "9\\r\\nINFO forged" + new string('x', 400), StringComparison.Ordinal));
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      QqqCompatibilityProblem problem = Assert.Single((await client.CheckCompatibilityAsync()).Problems);
      Assert.DoesNotContain(problem.Detail, char.IsControl);
      Assert.StartsWith("API version '9  INFO forged", problem.Detail, StringComparison.Ordinal);
      Assert.InRange(problem.Detail.Length, 1, 160);
      Assert.Contains("...'", problem.Detail, StringComparison.Ordinal);
   }



   /// <summary>
   /// What "printable" means, value by value. Line breaks of every kind (including U+2028 and U+2029), the characters
   /// that reorder or hide text (bidirectional embeddings, overrides, isolates and marks, zero-width characters, tag
   /// characters), private-use and unassigned code points all become spaces; ordinary text, including characters
   /// outside the basic plane, is kept; a surrogate without its partner becomes the replacement character; and the cut
   /// comes after 80 UTF-16 units without ever splitting a surrogate pair.
   /// </summary>
   [Fact]
   public void PrintableReplacesEverythingThatCouldBreakOrReorderALineAndNeverSplitsAPair()
   {
      (string? Value, string Shown)[] cases =
      [
         (null, "(missing)"),
         ("", "(missing)"),
         ("2.4.0", "2.4.0"),
         ("/qbit/printing caf\u00E9 \u5370\u5237 \U0001F5A8", "/qbit/printing caf\u00E9 \u5370\u5237 \U0001F5A8"),
         ("a\r\nb\tc\0d\u0085e", "a  b c d e"),
         ("a\u2028b\u2029c", "a b c"),
         ("a\u202Ab\u202Bc\u202Cd\u202De\u202Ef", "a b c d e f"),
         ("a\u2066b\u2067c\u2068d\u2069e", "a b c d e"),
         ("a\u200Eb\u200Fc\u061Cd", "a b c d"),
         ("a\u200Bb\u200Cc\u200Dd\uFEFFe\u00ADf", "a b c d e f"),
         ("a\U000E0001b\U000E007Fc", "a b c"),
         ("a\uE000b\U000F0000c\u0378d", "a b c d"),
         ("a\uD83Db\uDDA8c", "a\uFFFDb\uFFFDc"),
         (new string('x', 80), new string('x', 80)),
         (new string('x', 81), new string('x', 80) + "..."),
         (new string('x', 79) + "\U0001F5A8", new string('x', 79) + "..."),
         (new string('x', 78) + "\U0001F5A8", new string('x', 78) + "\U0001F5A8"),
         (new string('x', 78) + "\U0001F5A8y", new string('x', 78) + "\U0001F5A8..."),
      ];
      foreach((string? value, string shown) in cases)
      { Assert.Equal(shown, QqqCompatibilityProblem.Printable(value)); }
   }



   /// <summary>A 64 KiB value full of line breaks comes out as one short line: 80 units and the mark of the cut, with nothing in it that could start a new line or change direction.</summary>
   [Fact]
   public void PrintableBoundsAVeryLongHostileValue()
   {
      string hostile = string.Concat(Enumerable.Repeat("9\r\nINFO forged line\u2028\u202E", 65_536 / 21 + 1));
      Assert.True(hostile.Length >= 65_536);
      string shown = QqqCompatibilityProblem.Printable(hostile);
      Assert.Equal(83, shown.Length);
      Assert.StartsWith("9  INFO forged line  9  INFO forged line  ", shown, StringComparison.Ordinal);
      Assert.EndsWith("...", shown, StringComparison.Ordinal);
      Assert.All(shown, character => Assert.True(IsPlain(character)));
   }



   /// <summary>
   /// Every value the sign-in description supplies reaches a problem's detail only in printable form, because the wizard
   /// shows that detail before anyone has signed in. Each row makes one value hostile (line breaks, a line separator, a
   /// right-to-left override) and expects the sentence about it to carry the cleaned value and nothing else.
   /// </summary>
   [Theory]
   [InlineData("type", "authentication type 'x  y z w' is not supported; only TABLE_BASED and OAUTH2 are")]
   [InlineData("scopes", "scopes 'x  y z w' do not include openid")]
   [InlineData("flow", "grant flow is 'x  y z w', expected AUTHORIZATION_CODE_PKCE_SESSION")]
   [InlineData("codeChallengeMethod", "code challenge method is 'x  y z w', expected S256")]
   [InlineData("prompt", "prompt is 'x  y z w', expected login")]
   [InlineData("nonceValidation", "nonce validation policy 'x  y z w' is not one this client supports (expected REQUIRED, OPTIONAL or absent)")]
   [InlineData("issuer", "issuer 'x  y z w' is not a plain HTTPS address")]
   [InlineData("discoveryUrl", "discovery address 'x  y z w' is not a plain HTTPS address")]
   [InlineData("redirectUris", "redirect address(es) not an HTTPS app link or dotted private scheme: x  y z w, x  y z w")]
   public async Task EachSignInValueReachesTheDetailOnlyInPrintableForm(string member, string detail)
   {
      const string Hostile = "x\r\ny\u2028z\u202Ew";
      JsonObject metadata = JsonNode.Parse(ScriptedServer.OAuth)!.AsObject();
      JsonObject values = metadata["values"]!.AsObject();
      JsonObject grant = values["nativeSessionLogin"]!.AsObject();
      switch(member)
      {
         case "type":
            metadata["type"] = Hostile;
            break;
         case "scopes":
            values["scopes"] = Hostile;
            break;
         case "redirectUris":
            grant["redirectUris"] = new JsonArray(Hostile, Hostile);
            break;
         default:
            grant[member] = Hostile;
            break;
      }
      var info = new List<string>();
      using var server = new ScriptedServer();
      server.Add(ScriptedServer.Discovery).Add(metadata.ToJsonString());
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: new QqqSessionDiagnostics(info.Add));
      QqqCompatibilityProblem problem = Assert.Single((await client.CheckCompatibilityAsync()).Problems);
      Assert.Equal(QqqCompatibilityIssue.SignInUnavailable, problem.Issue);
      Assert.Equal(detail, problem.Detail);
      Assert.Equal($"Compatibility check found 1 problem(s): SignInUnavailable ({detail}).", Assert.Single(info, line => line.StartsWith("Compatibility check found", StringComparison.Ordinal)));
   }



   /// <summary>
   /// When every value is hostile and long at once, with the most redirect addresses a grant may list, the joined detail
   /// is still one bounded line: 512 units and the mark of the cut. The printing extension version shown beside the
   /// result is bounded and printable in the same way.
   /// </summary>
   [Fact]
   public async Task JoinedDetailAndDisplayedVersionAreBoundedHoweverMuchTheServerSends()
   {
      string hostile = string.Concat(Enumerable.Repeat("9\r\nINFO forged line\u2028\u202E", 60));
      JsonObject metadata = JsonNode.Parse(ScriptedServer.OAuth)!.AsObject();
      JsonObject values = metadata["values"]!.AsObject();
      JsonObject grant = values["nativeSessionLogin"]!.AsObject();
      values["scopes"] = hostile;
      foreach(string member in new[] { "flow", "codeChallengeMethod", "prompt", "nonceValidation", "issuer", "discoveryUrl" })
      { grant[member] = hostile; }
      grant["redirectUris"] = new JsonArray([.. Enumerable.Range(0, 32).Select(index => (JsonNode?)(index + hostile[..500]))]);
      using var server = new ScriptedServer();
      server.Add(ScriptedServer.Discovery).Add(metadata.ToJsonString())
         .Add(ScriptedServer.Discovery.Replace("2.4.0", JsonEncodedText.Encode(hostile).ToString(), StringComparison.Ordinal));
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);

      QqqCompatibilityProblem signIn = Assert.Single((await client.CheckCompatibilityAsync()).Problems);
      Assert.Equal(QqqCompatibilityIssue.SignInUnavailable, signIn.Issue);
      Assert.Equal(515, signIn.Detail.Length);
      Assert.StartsWith("grant flow is '9  INFO forged line  9  INFO forged line  ", signIn.Detail, StringComparison.Ordinal);
      Assert.EndsWith("...", signIn.Detail, StringComparison.Ordinal);
      Assert.All(signIn.Detail, character => Assert.True(IsPlain(character)));

      QqqServerCompatibility versioned = await client.CheckCompatibilityAsync();
      string version = Assert.IsType<string>(versioned.Discovery!.PrintingExtensionVersion);
      Assert.Equal(83, version.Length);
      Assert.StartsWith("9  INFO forged line  ", version, StringComparison.Ordinal);
      Assert.All(version, character => Assert.True(IsPlain(character)));
      Assert.All(versioned.Problems, problem => Assert.All(problem.Detail, character => Assert.True(IsPlain(character))));
   }



   /// <summary>
   /// The printing extension version offered for display is null whenever there is nothing to show (no printing
   /// extension, two of them, or one that states no version or an empty one), so an absent version can never be
   /// mistaken for a value; the text that reports the absence words it itself. A version that is present is returned
   /// in printable form.
   /// </summary>
   [Fact]
   public async Task PrintingExtensionVersionIsNullWhenAbsentAndPrintableWhenPresent()
   {
      static QqqApiDiscovery Advertising(params QqqApiDiscovery.Extension[] extensions) => new() { Extensions = extensions };
      var printing = new QqqApiDiscovery.Extension("printing", "2.4.0", "/qbit/printing");
      Assert.Null(Advertising().PrintingExtensionVersion);
      Assert.Null(Advertising(new QqqApiDiscovery.Extension("other", "2.4.0", "/qbit/other")).PrintingExtensionVersion);
      Assert.Null(Advertising(printing, printing).PrintingExtensionVersion);
      Assert.Null(Advertising(printing with { Version = null! }).PrintingExtensionVersion);
      Assert.Null(Advertising(printing with { Version = "" }).PrintingExtensionVersion);
      Assert.Equal("2.4.0", Advertising(printing).PrintingExtensionVersion);
      Assert.Equal("2  4", Advertising(printing with { Version = "2\r\n4" }).PrintingExtensionVersion);

      using var server = new ScriptedServer();
      server.Add(ScriptedServer.Discovery.Replace("\"version\":\"2.4.0\",", "", StringComparison.Ordinal));
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      QqqServerCompatibility result = await client.CheckCompatibilityAsync();
      Assert.Null(result.Discovery!.PrintingExtensionVersion);
      Assert.Equal("printing extension version '(missing)' is not a plain 1.x or 2.x release", Assert.Single(result.Problems).Detail);
   }



   /// <summary>A detail is cut one unit early when the limit falls inside a surrogate pair, so the cut never leaves half a character.</summary>
   [Fact]
   public void DetailCutNeverSplitsASurrogatePair()
   {
      var whole = new QqqCompatibilityProblem(QqqCompatibilityIssue.SignInUnavailable, new string('x', 512));
      var cut = new QqqCompatibilityProblem(QqqCompatibilityIssue.SignInUnavailable, new string('x', 513));
      var pair = new QqqCompatibilityProblem(QqqCompatibilityIssue.SignInUnavailable, new string('x', 511) + "\U0001F5A8");
      Assert.Equal(new string('x', 512), whole.Detail);
      Assert.Equal(new string('x', 512) + "...", cut.Detail);
      Assert.Equal(new string('x', 511) + "...", pair.Detail);
      Assert.Equal(whole, new QqqCompatibilityProblem(QqqCompatibilityIssue.SignInUnavailable, new string('x', 512)));
   }



   /// <summary>True for a character that cannot end a line, reorder text or hide: not a control, format, separator or surrogate character.</summary>
   private static bool IsPlain(char character) => char.GetUnicodeCategory(character) is not (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
      or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator or System.Globalization.UnicodeCategory.Surrogate);



   /// <summary>An OAuth2 server whose native grant is valid and lists the host's callback offers the browser; without a callback to check, the grant alone decides. Both requests are public even though the client holds a session and a bound device, and both are left alone.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task OAuthServerWithAUsableGrantOffersTheBrowser(bool checkCallback)
   {
      using ScriptedServer server = BoundServer().Add(ScriptedServer.Discovery).Add(ScriptedServer.OAuth);
      using QqqSessionClient client = await BoundClientAsync(server);
      QqqServerCompatibility result = await client.CheckCompatibilityAsync(checkCallback ? Redirect : null);
      Assert.True(result.IsCompatible);
      Assert.Equal(QqqSignInMethods.Browser, result.SignInMethods);
      await AssertCheckWasPublicAndLeftTheSessionAloneAsync(client, server, "/qqq/v1/version", "/qqq/v1/metaData/authentication");
   }



   /// <summary>
   /// A server that passes discovery but offers no sign-in this agent implements fails at the sign-in stage with the
   /// reason: an unknown authentication type, a grant that fails the sign-in rules, an API too old for the native grant,
   /// a callback the server does not list or that is not a permitted form, or a sign-in description that cannot be read
   /// or that demands authorization. The client holds a session and a bound device: neither request carries any of it,
   /// and whatever the server answers, the session is left as it was.
   /// </summary>
   [Theory]
   [InlineData("type", "authentication type 'TOKEN' is not supported; only TABLE_BASED and OAUTH2 are")]
   [InlineData("untyped", "authentication type '(missing)' is not supported; only TABLE_BASED and OAUTH2 are")]
   [InlineData("prompt", "prompt is 'none', expected login")]
   [InlineData("grant", "no nativeSessionLogin grant is published")]
   [InlineData("api", "API version '1.8.0' is not a plain 1.9+ release (1.9 is needed for native browser sign-in)")]
   [InlineData("feature", "the nativeSessionLogin feature is not advertised, so browser sign-in cannot be used")]
   [InlineData("unlisted", "the server's native session grant does not list the callback io.qrun.other:/oauth/callback")]
   [InlineData("loopback", "the server's native session grant does not list the callback http://127.0.0.1:49152/callback")]
   [InlineData("page", "the sign-in description answer was not a valid QQQ document")]
   [InlineData("refused", "the server refused or redirected the sign-in description request")]
   [InlineData("unauthorized", "the server asks for authorization before answering the sign-in description request")]
   public async Task ServerWithoutAUsableSignInFailsAtTheSignInStage(string fault, string detail)
   {
      string discovery = fault switch
      {
         "api" => ScriptedServer.Discovery.Replace("1.10.0", "1.8.0", StringComparison.Ordinal),
         "feature" => ScriptedServer.Discovery.Replace("\"nativeSessionLogin\",", "", StringComparison.Ordinal),
         _ => ScriptedServer.Discovery,
      };
      using ScriptedServer server = BoundServer();
      server.Add(discovery);
      server.AddResponse(() => fault switch
      {
         "type" => ScriptedServer.Json("""{"name":"token","type":"TOKEN"}"""),
         "untyped" => ScriptedServer.Json("""{"name":"unnamed"}"""),
         "prompt" => ScriptedServer.Json(ScriptedServer.OAuth.Replace("\"login\"", "\"none\"", StringComparison.Ordinal)),
         "grant" => ScriptedServer.Json(ScriptedServer.OAuth.Replace("nativeSessionLogin", "nativeBearer", StringComparison.Ordinal)),
         "page" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>sign in</html>", Encoding.UTF8, "text/html") },
         "refused" => ScriptedServer.Json("{}", HttpStatusCode.InternalServerError),
         "unauthorized" => ScriptedServer.Json("{}", HttpStatusCode.Unauthorized),
         _ => ScriptedServer.Json(ScriptedServer.OAuth),
      });
      Uri? callback = fault switch
      {
         "unlisted" => new Uri("io.qrun.other:/oauth/callback?state=not-shown"),
         "loopback" => new Uri("http://127.0.0.1:49152/callback"),
         _ => Redirect,
      };
      using QqqSessionClient client = await BoundClientAsync(server);
      QqqServerCompatibility result = await client.CheckCompatibilityAsync(callback);
      QqqCompatibilityProblem problem = Assert.Single(result.Problems);
      Assert.Equal(QqqCompatibilityIssue.SignInUnavailable, problem.Issue);
      Assert.Contains(detail, problem.Detail, StringComparison.Ordinal);
      Assert.DoesNotContain("not-shown", problem.Detail, StringComparison.Ordinal);
      Assert.Equal(QqqCompatibilityStage.SignIn, result.FailedStage);
      Assert.Equal(QqqSignInMethods.None, result.SignInMethods);
      Assert.NotNull(result.Discovery);
      await AssertCheckWasPublicAndLeftTheSessionAloneAsync(client, server, "/qqq/v1/version", "/qqq/v1/metaData/authentication");
   }



   /// <summary>
   /// A certificate refused while the sign-in description is read is reported exactly as one refused at discovery: a
   /// secure connection failure at the security stage, with the certificate itself, so the wizard can show what an
   /// exception would trust. The discovery that was read before it is still returned.
   /// </summary>
   [Fact]
   public async Task CertificateRefusedAtTheSignInDescriptionIsReportedWithTheCertificate()
   {
      using X509Certificate2 certificate = SelfSigned("CN=Synthetic Local Authority");
      using var server = new ScriptedServer();
      QqqSessionClient? client = null;
      server.Add(ScriptedServer.Discovery).AddAsync(_ =>
      {
         Assert.False(client!.EvaluateCertificate(new Uri("https://printing.example.invalid"), certificate, SslPolicyErrors.RemoteCertificateChainErrors));
         return Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established.",
            new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback.")));
      });
      using(client = new QqqSessionClient(ScriptedServer.Origin, server))
      {
         QqqServerCompatibility result = await client.CheckCompatibilityAsync();
         QqqCompatibilityProblem problem = Assert.Single(result.Problems);
         Assert.Equal(QqqCompatibilityIssue.SecureConnectionFailed, problem.Issue);
         Assert.Equal("Windows does not trust the authority that issued the server's certificate", problem.Detail);
         Assert.Equal(QqqCompatibilityStage.Security, result.FailedStage);
         Assert.NotNull(result.Discovery);
         Assert.Equal(QqqSignInMethods.None, result.SignInMethods);
         QqqCertificateRejection rejection = Assert.IsType<QqqCertificateRejection>(result.Certificate);
         Assert.Equal(certificate.Thumbprint, rejection.Thumbprint);
         Assert.True(rejection.IsUntrustedAuthority);
      }
   }



   /// <summary>A server that stops answering between discovery and the sign-in description is unreachable, not "no sign-in method": the operator should retry, not reconfigure, and no certificate is shown beside a failure that no certificate caused.</summary>
   [Fact]
   public async Task ConnectionLostBeforeTheSignInDescriptionIsUnreachable()
   {
      using var server = new ScriptedServer();
      server.Add(ScriptedServer.Discovery).AddFailure(() => new HttpRequestException(HttpRequestError.ConnectionError, "Connection reset."));
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      QqqServerCompatibility result = await client.CheckCompatibilityAsync();
      Assert.Equal(QqqCompatibilityIssue.ServerUnreachable, Assert.Single(result.Problems).Issue);
      Assert.Equal(QqqCompatibilityStage.Connection, result.FailedStage);
      Assert.NotNull(result.Discovery);
      Assert.Null(result.Certificate);
   }



   /// <summary>
   /// The caller's own cancellation is not a finding about the server: it propagates, at either request, so a closed
   /// wizard does not show "unreachable". It also wins when another failure surfaces at the same moment, whether that
   /// failure would have been a finding (the connection dropping as the request is abandoned) or would have propagated
   /// on its own (a programming error): the caller asked to stop and is told it stopped.
   /// </summary>
   [Theory]
   [InlineData(0, "cancellation")]
   [InlineData(1, "cancellation")]
   [InlineData(0, "connection-failure")]
   [InlineData(1, "connection-failure")]
   [InlineData(0, "unrelated-failure")]
   [InlineData(1, "unrelated-failure")]
   public async Task CallerCancellationPropagatesInsteadOfBecomingAProblem(int requestsBeforeCancel, string surfaced)
   {
      using var cancellation = new CancellationTokenSource();
      using var server = new ScriptedServer();
      if(requestsBeforeCancel == 1)
      { server.Add(ScriptedServer.Discovery); }
      server.AddAsync(async token =>
      {
         await cancellation.CancelAsync();
         if(string.Equals(surfaced, "connection-failure", StringComparison.Ordinal))
         { throw new HttpRequestException(HttpRequestError.ConnectionError, "The connection was reset as the request was abandoned."); }
         if(string.Equals(surfaced, "unrelated-failure", StringComparison.Ordinal))
         { throw new InvalidOperationException("Synthetic programming error."); }
         token.ThrowIfCancellationRequested();
         return ScriptedServer.Json("{}");
      });
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CheckCompatibilityAsync(cancellationToken: cancellation.Token));
   }



   /// <summary>A failure that says nothing about the server, such as a programming error in the transport, is not disguised as a compatibility problem.</summary>
   [Theory]
   [InlineData(0)]
   [InlineData(1)]
   public async Task UnrelatedFailurePropagates(int requestsBeforeFailure)
   {
      using var server = new ScriptedServer();
      if(requestsBeforeFailure == 1)
      { server.Add(ScriptedServer.Discovery); }
      server.AddFailure(() => new InvalidOperationException("Synthetic programming error."));
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      await Assert.ThrowsAsync<InvalidOperationException>(() => client.CheckCompatibilityAsync());
   }



   /// <summary>A check that passed is not repeated by the sign-in that follows: the password request is the only further request, exactly as after <c>DiscoverAsync</c>.</summary>
   [Fact]
   public async Task PassedCheckIsReusedBySignIn()
   {
      using ScriptedServer server = ScriptedServer.WithTable().Add("""{"uuid":"session"}""");
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      Assert.True((await client.CheckCompatibilityAsync()).IsCompatible);
      await client.SignInWithPasswordAsync("synthetic-user", "synthetic-password");
      Assert.Equal(new[] { "/qqq/v1/version", "/qqq/v1/metaData/authentication", "/qqq/v1/manageSession" }, server.Requests.Select(request => request.Uri.AbsolutePath), StringComparer.Ordinal);
   }



   /// <summary>Each check asks the server again, so a retry after the operator fixed the server sees the fix instead of the earlier answer.</summary>
   [Fact]
   public async Task EveryCheckAsksTheServerAgain()
   {
      using var server = new ScriptedServer();
      server.Add("{}", HttpStatusCode.ServiceUnavailable).Add(ScriptedServer.Discovery).Add(ScriptedServer.Table).Add(ScriptedServer.Discovery.Replace("2.4.0", "1.0.0", StringComparison.Ordinal));
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      Assert.True((await client.CheckCompatibilityAsync()).Has(QqqCompatibilityIssue.NotQqqServer));
      Assert.True((await client.CheckCompatibilityAsync()).IsCompatible);
      Assert.True((await client.CheckCompatibilityAsync()).Has(QqqCompatibilityIssue.PrintingExtensionTooOld));
      Assert.Equal(4, server.Requests.Count);
   }



   /// <summary>The existing contract is untouched: a 1.x printing extension still passes <c>DiscoverAsync</c> (sign-in works against it), and an unusable document still throws the one generic configuration failure.</summary>
   [Fact]
   public async Task DiscoverAsyncKeepsItsContract()
   {
      using var server = new ScriptedServer();
      server.Add(ScriptedServer.Discovery.Replace("2.4.0", "1.0.0", StringComparison.Ordinal)).Add(ScriptedServer.Discovery.Replace("\"printing\"", "\"other\"", StringComparison.Ordinal));
      using var accepted = new QqqSessionClient(ScriptedServer.Origin, server);
      Assert.Equal("1.0.0", (await accepted.DiscoverAsync()).PrintingExtensionVersion);
      using var refused = new QqqSessionClient(ScriptedServer.Origin, server);
      QqqSessionException exception = await Assert.ThrowsAsync<QqqSessionException>(() => refused.DiscoverAsync());
      Assert.Equal(QqqSessionException.FailureKind.ConfigurationRequired, exception.Failure);
      Assert.Equal("This server does not advertise the required QQQ printing and sign-in capabilities.", exception.Message);
   }



   /// <summary>A server whose first four replies sign a user in with a password and bind device-1; a test queues what the check should see after them.</summary>
   private static ScriptedServer BoundServer() => ScriptedServer.SignedIn().Add(ScriptedServer.Identity);



   /// <summary>A client that is signed in and has a device bound, so that a check which sent or disturbed any of that would be caught.</summary>
   private static async Task<QqqSessionClient> BoundClientAsync(ScriptedServer server, QqqSessionDiagnostics? diagnostics = null)
   {
      var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: diagnostics);
      await client.SignInWithPasswordAsync("synthetic-user", "synthetic-password");
      await client.BindDeviceAsync(new QqqDeviceCredential("device-1", "synthetic-proof"));
      Assert.Equal(SignInRequests, server.Requests.Count);
      Assert.Equal("device-1", server.Requests[^1].DeviceId);
      return client;
   }



   /// <summary>
   /// Requires that the check made exactly the given requests, none of them carrying the session cookie, the device
   /// proof or any other credential, and that it left the client as it found it: still signed in with the same session,
   /// and still bound, which a device request proves by going out with that cookie and that device's proof. Had the
   /// check cleared or replaced the session, that request would be refused locally before it was sent.
   /// </summary>
   private static async Task AssertCheckWasPublicAndLeftTheSessionAloneAsync(QqqSessionClient client, ScriptedServer server, params string[] paths)
   {
      ScriptedRequest[] check = [.. server.Requests.Skip(SignInRequests)];
      Assert.Equal(paths, check.Select(request => request.Uri.AbsolutePath), StringComparer.Ordinal);
      Assert.All(check, request => Assert.True(request.IsPublic));
      Assert.True(client.IsAuthenticated);
      Assert.Equal("session", client.ExportCredential().SessionToken);
      server.Add("""{"jobs":[]}""");
      using HttpResponseMessage response = await client.SendPrintingAsync(HttpMethod.Get, "device/jobs/queued");
      ScriptedRequest device = server.Requests[^1];
      Assert.Equal("/qqq/v1/qbit/printing/device/jobs/queued", device.Uri.AbsolutePath);
      Assert.Contains("sessionUUID=session", device.Cookie, StringComparison.Ordinal);
      Assert.Equal("device-1", device.DeviceId);
      Assert.Equal("synthetic-proof", device.DeviceToken);
   }



   /// <summary>
   /// A hand-written server for the redirect test. It answers discovery on the session origin with a redirect to
   /// another host and can serve that host too. Like the platform handler it stands in for, it follows its own redirect
   /// when the transport policy allows that, and it records every address it was asked for, so a followed redirect
   /// cannot go unnoticed.
   /// </summary>
   private sealed class RedirectingServer : HttpMessageHandler
   {
      private static readonly Uri Target = new("https://elsewhere.example.invalid/qqq/v1/version");



      /// <summary>The policy the client handed the transport; read at each request, as a platform handler is configured by it.</summary>
      internal Func<QqqTransportPolicy?>? Policy { get; set; }



      /// <summary>Every address a request arrived for, in order.</summary>
      internal List<Uri> Asked { get; } = [];



      /// <summary>True once the redirect's target was asked, whether the client asked it or this server followed its own redirect.</summary>
      internal bool TargetAsked { get; private set; }



      /// <summary>Serves the target when it is asked for; otherwise answers with the redirect, following it first when the policy allows.</summary>
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         Asked.Add(request.RequestUri!);
         if(request.RequestUri == Target || Policy?.Invoke()?.FollowRedirects == true)
         {
            TargetAsked = true;
            return Task.FromResult(ScriptedServer.Json(ScriptedServer.Discovery));
         }
         var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
         redirect.Headers.Location = Target;
         return Task.FromResult(redirect);
      }
   }



   /// <summary>A throwaway self-signed certificate for the policy tests; it is never installed anywhere.</summary>
   private static X509Certificate2 SelfSigned(string subject)
   {
      using var key = ECDsa.Create();
      var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
      return request.CreateSelfSigned(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
   }
}
