/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// The edges of the session client: what it refuses to be constructed with, what it refuses to send, and what it
/// refuses to accept back. Each case is one specific malformed input or response, and each must end in the documented
/// refusal with the session left in the documented state.
/// </summary>
public sealed class QqqSessionBoundaryTests
{
   private static readonly Uri Redirect = new(ScriptedServer.Callback);



   /// <summary>A client can only be bound to a bare HTTPS origin, or to loopback HTTP when that was explicitly allowed; anything carrying credentials, a path, a query or a fragment is refused.</summary>
   [Theory]
   [InlineData("http://printing.example.invalid", true)]
   [InlineData("http://127.0.0.1:8080", false)]
   [InlineData("https://user:secret@printing.example.invalid", true)]
   [InlineData("https://printing.example.invalid/qqq", true)]
   [InlineData("https://printing.example.invalid/?tenant=1", true)]
   [InlineData("https://printing.example.invalid/#top", true)]
   [InlineData("ftp://printing.example.invalid", true)]
   [InlineData("/relative", true)]
   public void OnlyABareSecureOriginIsAccepted(string origin, bool allowLoopbackHttp)
   {
      using var server = new ScriptedServer();
      Assert.Throws<ArgumentException>(() => new QqqSessionClient(new Uri(origin, UriKind.RelativeOrAbsolute), server, allowLoopbackHttp: allowLoopbackHttp));
   }



   /// <summary>An accepted origin is reduced to scheme, host and port, and loopback HTTP is accepted only with the explicit switch.</summary>
   [Fact]
   public void AcceptedOriginIsNormalized()
   {
      using var server = new ScriptedServer();
      using var secure = new QqqSessionClient(new Uri("HTTPS://Printing.Example.Invalid:8443"), server);
      using var loopback = new QqqSessionClient(new Uri("http://127.0.0.1:8080"), server, allowLoopbackHttp: true);
      Assert.Equal(new Uri("https://printing.example.invalid:8443/"), secure.Origin);
      Assert.Equal(new Uri("http://127.0.0.1:8080/"), loopback.Origin);
      Assert.False(secure.IsAuthenticated);
   }



   /// <summary>A user name or password that could change the meaning of the Basic credential (a colon in the name, control characters, a line break, absurd length) is refused before any request.</summary>
   [Theory]
   [InlineData("user:name", "password")]
   [InlineData("user\tname", "password")]
   [InlineData("long", "password")]
   [InlineData("user", "pass\nword")]
   [InlineData("user", "pass\rword")]
   [InlineData("user", "long")]
   [InlineData(" ", "password")]
   [InlineData("user", " ")]
   public async Task UnsafeSignInInputIsRefusedBeforeAnyRequest(string username, string password)
   {
      using var server = new ScriptedServer();
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      await Assert.ThrowsAnyAsync<ArgumentException>(() => client.SignInWithPasswordAsync(
         string.Equals(username, "long", StringComparison.Ordinal) ? new string('u', 1025) : username,
         string.Equals(password, "long", StringComparison.Ordinal) ? new string('p', 4097) : password));
      Assert.Empty(server.Requests);
   }



   /// <summary>A password is never sent to a server that signs in through an identity provider, and the reason is logged.</summary>
   [Fact]
   public async Task PasswordIsNotSentToAnOAuthServer()
   {
      var info = new List<string>();
      using var server = ScriptedServer.WithOAuth();
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: new QqqSessionDiagnostics(info.Add));
      QqqAuthenticationMetadata metadata = await client.GetAuthenticationMetadataAsync();
      Assert.Equal("OAUTH2", metadata.Type);
      Assert.Equal("qqq-client", metadata.Values!.ClientId);
      QqqSessionException refused = await Assert.ThrowsAsync<QqqSessionException>(() => client.SignInWithPasswordAsync("synthetic-user", "synthetic-password"));
      Assert.Equal(QqqSessionException.FailureKind.ConfigurationRequired, refused.Failure);
      Assert.Contains("Password sign-in refused locally: the server authenticates with OAUTH2, not TABLE_BASED.", info, StringComparer.Ordinal);
      Assert.Equal(2, server.Requests.Count);
      Assert.All(server.Requests, request => Assert.True(request.IsPublic));
   }



   /// <summary>Signing out with no session makes no request and says so; exporting a credential without a session is refused.</summary>
   [Fact]
   public async Task SignOutWithoutASessionOnlyClearsLocalState()
   {
      var info = new List<string>();
      using var server = new ScriptedServer();
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: new QqqSessionDiagnostics(info.Add));
      await client.SignOutAsync();
      Assert.Empty(server.Requests);
      Assert.Contains("Sign-out requested with no held session; clearing local state only.", info, StringComparer.Ordinal);
      Assert.Equal(QqqSessionException.FailureKind.AuthenticationRequired, Assert.Throws<QqqSessionException>(() => client.ExportCredential()).Failure);
   }



   /// <summary>
   /// A sign-in response is adopted only when its session is unambiguous and well formed. A session id with characters
   /// that could alter a cookie header, an oversized Set-Cookie header, a cookie for another host, and an empty session
   /// cookie each leave the client signed out.
   /// </summary>
   [Theory]
   [InlineData("""{"uuid":"bad token;x"}""", null, QqqSessionException.FailureKind.InvalidResponse, "Session response rejected: the session id is not well formed.")]
   [InlineData("{}", "oversized", QqqSessionException.FailureKind.InvalidResponse, null)]
   [InlineData("{}", "sessionUUID=stolen; Path=/; Domain=.elsewhere.invalid", QqqSessionException.FailureKind.InvalidResponse, null)]
   [InlineData("{}", "sessionUUID=; Path=/", QqqSessionException.FailureKind.AuthenticationRequired, "The server returned an empty sessionUUID cookie; the session has ended and sign-in is required.")]
   public async Task UnusableSessionInASignInResponseIsNeverAdopted(string body, string? cookie, QqqSessionException.FailureKind failure, string? line)
   {
      var info = new List<string>();
      using ScriptedServer server = ScriptedServer.WithTable().Add(body, cookie: string.Equals(cookie, "oversized", StringComparison.Ordinal) ? "sessionUUID=" + new string('s', 8192) + "; Path=/" : cookie);
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: new QqqSessionDiagnostics(info.Add));
      QqqSessionException refused = await Assert.ThrowsAsync<QqqSessionException>(() => client.SignInWithPasswordAsync("synthetic-user", "synthetic-password"));
      Assert.Equal(failure, refused.Failure);
      Assert.False(client.IsAuthenticated);
      if(line is not null)
      { Assert.Contains(line, info, StringComparer.Ordinal); }
   }



   /// <summary>A printing response that tries to plant a different session cookie is refused, so a compromised route cannot swap the session the agent uses.</summary>
   [Fact]
   public async Task PrintingResponseCannotReplaceTheSession()
   {
      using ScriptedServer server = ScriptedServer.SignedIn().Add("{}", cookie: "sessionUUID=another-session; Path=/").Add("{}", cookie: "sessionUUID=session; Path=/");
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      await client.SignInWithPasswordAsync("synthetic-user", "synthetic-password");
      QqqSessionException refused = await Assert.ThrowsAsync<QqqSessionException>(() => client.SendPrintingAsync(HttpMethod.Get, "admin/devices"));
      Assert.Equal(QqqSessionException.FailureKind.InvalidResponse, refused.Failure);
      using HttpResponseMessage accepted = await client.SendPrintingAsync(HttpMethod.Get, "admin/devices");
      Assert.Equal("session", client.ExportCredential().SessionToken);
   }



   /// <summary>The client's single send path refuses any address that is not on its own origin, whatever asked for it, before a request exists.</summary>
   [Theory]
   [InlineData("https://elsewhere.invalid/qqq/v1/version")]
   [InlineData("http://printing.example.invalid/qqq/v1/version")]
   [InlineData("https://printing.example.invalid:8443/qqq/v1/version")]
   public async Task SendPathRefusesAnotherOrigin(string address)
   {
      var info = new List<string>();
      using var server = new ScriptedServer();
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: new QqqSessionDiagnostics(info.Add));
      await Assert.ThrowsAsync<ArgumentException>(() => client.SendCoreAsync(new Uri(address + "?token=never-logged"), HttpMethod.Get, null, 1024, "sessionUUID=session", null, null, CancellationToken.None));
      Assert.Empty(server.Requests);
      Assert.Contains($"Refused a request to {address}: it is not on the session origin printing.example.invalid.", info, StringComparer.Ordinal);
   }



   /// <summary>A response body that keeps arriving past the bound, without ever announcing its length, is cut off and rejected instead of filling memory.</summary>
   [Fact]
   public async Task UnannouncedOversizedBodyIsRejectedWhileReading()
   {
      var info = new List<string>();
      using var body = new FeedStream();
      foreach(int _ in Enumerable.Range(0, 9))
      { body.Feed(new byte[8_192]); }
      using var server = new ScriptedServer();
      server.AddResponse(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: new QqqSessionDiagnostics(info.Add));
      QqqSessionException refused = await Assert.ThrowsAsync<QqqSessionException>(() => client.DiscoverAsync());
      Assert.Equal(QqqSessionException.FailureKind.InvalidResponse, refused.Failure);
      Assert.Contains("Response rejected: the body exceeded the 65536 byte bound while reading.", info, StringComparer.Ordinal);
   }



   /// <summary>A discovery document whose members have the wrong JSON types is an invalid response, and the log names the shape that was expected.</summary>
   [Fact]
   public async Task DiscoveryOfTheWrongShapeIsAnInvalidResponse()
   {
      var info = new List<string>();
      using var server = new ScriptedServer();
      server.Add("""{"apiVersion":"1.10.0","features":"all of them"}""");
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: new QqqSessionDiagnostics(info.Add));
      QqqSessionException refused = await Assert.ThrowsAsync<QqqSessionException>(() => client.DiscoverAsync());
      Assert.Equal(QqqSessionException.FailureKind.InvalidResponse, refused.Failure);
      Assert.Contains(info, line => line.StartsWith("Response rejected: the JSON did not match the expected QqqApiDiscovery shape", StringComparison.Ordinal));
   }



   /// <summary>
   /// Browser sign-in is refused, before any browser opens, when the server's native grant or its provider is not one
   /// this agent can use safely; the log names each shortfall. The loopback HTTP case shows an insecure origin can never
   /// start a browser sign-in.
   /// </summary>
   [Theory]
   [InlineData("table", "authentication type is 'TABLE_BASED', not OAUTH2")]
   [InlineData("client", "client id is missing or too long")]
   [InlineData("scopes", "scopes 'profile email' do not include openid")]
   [InlineData("no-redirects", "0 redirect address(es) listed; expected 1 to 32")]
   [InlineData("plain-redirect", "redirect address(es) not an HTTPS app link or dotted private scheme: http://127.0.0.1/callback")]
   [InlineData("insecure-origin", "the server origin is not HTTPS")]
   [InlineData("provider-endpoint", "Browser sign-in refused: the provider's authorization endpoint is not an HTTPS address on the issuer's origin.")]
   [InlineData("provider-plain-endpoint", "Browser sign-in refused: the provider's authorization endpoint is not an HTTPS address on the issuer's origin.")]
   [InlineData("provider-s256", "Browser sign-in refused: the provider does not list S256 among its supported code challenge methods.")]
   public async Task UnusableGrantOrProviderNeverOpensABrowser(string fault, string reason)
   {
      var info = new List<string>();
      string metadata = fault switch
      {
         "table" => ScriptedServer.Table,
         "client" => ScriptedServer.OAuth.Replace("\"clientId\":\"qqq-client\"", "\"clientId\":\"\"", StringComparison.Ordinal),
         "scopes" => ScriptedServer.OAuth.Replace("openid profile", "profile email", StringComparison.Ordinal),
         "no-redirects" => ScriptedServer.OAuth.Replace("\"redirectUris\":[\"io.qrun.qqq-print-agent:/oauth/callback\"]", "\"redirectUris\":[]", StringComparison.Ordinal),
         "plain-redirect" => ScriptedServer.OAuth.Replace("\"redirectUris\":[\"io.qrun.qqq-print-agent:/oauth/callback\"]", "\"redirectUris\":[\"http://127.0.0.1/callback\"]", StringComparison.Ordinal),
         _ => ScriptedServer.OAuth,
      };
      string provider = fault switch
      {
         "provider-endpoint" => ScriptedServer.Provider.Replace("https://id.example.invalid/authorize", "https://elsewhere.invalid/authorize", StringComparison.Ordinal),
         "provider-plain-endpoint" => ScriptedServer.Provider.Replace("https://id.example.invalid/authorize", "http://id.example.invalid/authorize", StringComparison.Ordinal),
         "provider-s256" => ScriptedServer.Provider.Replace("\"S256\"", "\"plain\"", StringComparison.Ordinal),
         _ => ScriptedServer.Provider,
      };
      using var server = new ScriptedServer();
      server.Add(ScriptedServer.Discovery).Add(metadata).Add(provider);
      Uri origin = string.Equals(fault, "insecure-origin", StringComparison.Ordinal) ? new Uri("http://127.0.0.1:18581") : ScriptedServer.Origin;
      using var client = new QqqSessionClient(origin, server, allowLoopbackHttp: true, diagnostics: new QqqSessionDiagnostics(info.Add));
      QqqSessionException refused = await Assert.ThrowsAsync<QqqSessionException>(() => client.BeginBrowserLoginAsync(Redirect));
      Assert.Equal(QqqSessionException.FailureKind.ConfigurationRequired, refused.Failure);
      Assert.Contains(info, line => line.Contains(reason, StringComparison.Ordinal));
      Assert.All(server.Requests, request => Assert.True(request.IsPublic));
   }



   /// <summary>
   /// A browser callback is refused, and its attempt used up, when the provider reports an error, when it carries no
   /// usable code, or when its query is missing or malformed. The provider's own error text reaches the log; no exchange
   /// request is made.
   /// </summary>
   [Theory]
   [InlineData("?error=access_denied&error_description=The+user+cancelled&state=STATE", "Browser sign-in refused by the identity provider: error 'access_denied' (The user cancelled).")]
   [InlineData("?error=server_error&state=STATE", "Browser sign-in refused by the identity provider: error 'server_error'.")]
   [InlineData("?state=STATE", "Browser callback refused: no usable authorization code was returned.")]
   [InlineData("?code=%20&state=STATE", "Browser callback refused: no usable authorization code was returned.")]
   [InlineData("?code=LONG&state=STATE", "Browser callback refused: no usable authorization code was returned.")]
   [InlineData("", "Browser sign-in completion failed (InvalidResponse): The server returned an invalid authentication or API response.")]
   [InlineData("?code&state=STATE", "Browser sign-in completion failed (InvalidResponse): The server returned an invalid authentication or API response.")]
   [InlineData("?code=one%0Atwo&state=STATE", "Browser sign-in completion failed (InvalidResponse): The server returned an invalid authentication or API response.")]
   public async Task UnusableCallbackIsRefusedWithoutAnExchange(string query, string line)
   {
      var info = new List<string>();
      using ScriptedServer server = ScriptedServer.WithOAuth().Add(ScriptedServer.Provider);
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: new QqqSessionDiagnostics(info.Add));
      QqqBrowserLogin attempt = await client.BeginBrowserLoginAsync(Redirect);
      string state = attempt.AuthorizationUri.Query[1..].Split('&').Single(pair => pair.StartsWith("state=", StringComparison.Ordinal))[6..];
      var callback = new Uri(ScriptedServer.Callback + query.Replace("STATE", state, StringComparison.Ordinal).Replace("LONG", new string('c', 4097), StringComparison.Ordinal));
      QqqSessionException refused = await Assert.ThrowsAsync<QqqSessionException>(() => client.CompleteBrowserLoginAsync(attempt, callback));
      Assert.Equal(QqqSessionException.FailureKind.InvalidResponse, refused.Failure);
      Assert.Contains(line, info, StringComparer.Ordinal);
      Assert.Equal(3, server.Requests.Count);
      Assert.False(client.IsAuthenticated);
   }



   /// <summary>Device and session credentials accept only values that are safe to place in a header: a bounded identifier alphabet and visible ASCII tokens, and the origin of a session must be given.</summary>
   [Theory]
   [InlineData("device id", "token")]
   [InlineData("device/1", "token")]
   [InlineData("long", "token")]
   [InlineData("device-1", "to ken")]
   [InlineData("device-1", "tokén")]
   [InlineData("device-1", "long")]
   public void CredentialsRejectValuesThatAreUnsafeInAHeader(string deviceId, string token)
   {
      string id = string.Equals(deviceId, "long", StringComparison.Ordinal) ? new string('d', 129) : deviceId;
      string secret = string.Equals(token, "long", StringComparison.Ordinal) ? new string('t', 4097) : token;
      Assert.Throws<ArgumentException>(() => new QqqDeviceCredential(id, secret));
      if(string.Equals(deviceId, "device-1", StringComparison.Ordinal))
      { Assert.Throws<ArgumentException>(() => new QqqSessionCredential(ScriptedServer.Origin, secret)); }
   }



   /// <summary>A session token containing a cookie separator or a quote is refused, and well-formed credentials keep exactly what they were given.</summary>
   [Fact]
   public void WellFormedCredentialsAreKeptAndSeparatorsRefused()
   {
      Assert.Throws<ArgumentException>(() => new QqqSessionCredential(ScriptedServer.Origin, "session;extra"));
      Assert.Throws<ArgumentException>(() => new QqqSessionCredential(ScriptedServer.Origin, "session\"quoted"));
      Assert.Throws<ArgumentNullException>(() => new QqqSessionCredential(null!, "session"));
      var device = new QqqDeviceCredential("Device_1.a-b", "synthetic-proof");
      Assert.Equal("Device_1.a-b", device.DeviceId);
      Assert.Equal("synthetic-proof", device.DeviceToken);
      Assert.Equal(ScriptedServer.Origin, new QqqSessionCredential(ScriptedServer.Origin, "session").Origin);
   }



   /// <summary>
   /// Capabilities are switched on only by a plain release at or above the version that introduced them: media
   /// negotiation at printing 2.3, printer approval at 2.4, provider sign-out at UI API 1.10 with its feature, and nonce
   /// validation by its feature alone. A pre-release or a non-standard path enables nothing.
   /// </summary>
   [Theory]
   [InlineData("2.4.0", "/qbit/printing", "1.10.0", true, true, true)]
   [InlineData("2.3.9", "/qbit/printing", "1.10.0", true, false, true)]
   [InlineData("2.2.0", "/qbit/printing", "1.9.0", false, false, false)]
   [InlineData("2.4.0-rc.1", "/qbit/printing", "1.10.0-rc.1", false, false, false)]
   [InlineData("2.4.0", "/other/printing", "2.0.0", false, false, false)]
   public void CapabilitiesFollowPlainReleaseVersions(string printing, string path, string api, bool media, bool approval, bool logout)
   {
      var discovery = new QqqApiDiscovery
      {
         ApiVersion = api,
         Features = [QqqApiDiscovery.NativeSessionLogoutFeature, QqqApiDiscovery.NonceValidationFeature],
         Extensions = [new QqqApiDiscovery.Extension("printing", printing, path)],
      };
      Assert.Equal(media, discovery.SupportsPrinterMedia());
      Assert.Equal(approval, discovery.SupportsPrinterApproval());
      Assert.Equal(logout, discovery.SupportsNativeSessionLogout);
      Assert.True(discovery.ValidatesNativeSessionNonce);
      Assert.False(new QqqApiDiscovery { ApiVersion = "1.10.0" }.SupportsNativeSessionLogout);
      Assert.False(new QqqApiDiscovery().ValidatesNativeSessionNonce);
   }
}
