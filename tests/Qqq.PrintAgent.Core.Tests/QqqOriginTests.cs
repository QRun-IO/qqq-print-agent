/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// An origin is kept, compared and shown in one form: scheme, the host's ASCII name and port. The host of a sign-in
/// provider is written by the server, and an operator is shown it before a trust decision, so these tests hold the
/// form to what it promises: a name outside ASCII appears as its "xn--" name, which is where the connection really
/// goes; characters that show as nothing are not in it; a host the platform cannot name in ASCII is no origin at all;
/// and the cases a reviewer probed as safe (trailing dot, IPv6 literal, short IPv4, default port) stay as they are.
/// </summary>
public sealed class QqqOriginTests
{
   /// <summary>
   /// The origin of an address is its scheme, ASCII host and port and nothing else: no user information, path, query
   /// or fragment. Letter case is folded and a default port is not shown. A look-alike letter (a Cyrillic letter in
   /// place of the "a" in "apple.com") or any other name outside ASCII is shown as the "xn--" name it is, and the
   /// "xn--" form itself is unchanged, so the two are one origin. A zero-width space, a byte-order mark and a soft hyphen are not part of
   /// the name the connection is made to and are gone; a full-width dot is the dot it stands for. A trailing dot is
   /// kept, because it is a different name to a certificate. An IPv6 literal keeps its brackets and its shortest
   /// form, and an IPv4 address written short or in hexadecimal is the full address.
   /// </summary>
   [Theory]
   [InlineData("https://dev.example.invalid:8443/qqq/v1/version?x=1#top", "https://dev.example.invalid:8443/", "dev.example.invalid:8443")]
   [InlineData("https://operator:secret@dev.example.invalid", "https://dev.example.invalid/", "dev.example.invalid")]
   [InlineData("https://DEV.Example.Invalid:443/", "https://dev.example.invalid/", "dev.example.invalid")]
   [InlineData("https://\u0430pple.com/realms/dev", "https://xn--pple-43d.com/", "xn--pple-43d.com")]
   [InlineData("https://xn--pple-43d.com:8443", "https://xn--pple-43d.com:8443/", "xn--pple-43d.com:8443")]
   [InlineData("https://XN--PPLE-43D.com", "https://xn--pple-43d.com/", "xn--pple-43d.com")]
   [InlineData("https://b\u00FCcher.example.invalid", "https://xn--bcher-kva.example.invalid/", "xn--bcher-kva.example.invalid")]
   [InlineData("https://id\u200B.example.invalid", "https://id.example.invalid/", "id.example.invalid")]
   [InlineData("https://i\uFEFFd.exam\u00ADple.invalid", "https://id.example.invalid/", "id.example.invalid")]
   [InlineData("https://id\uFF0Eexample\u3002invalid", "https://id.example.invalid/", "id.example.invalid")]
   [InlineData("https://id.example.invalid./", "https://id.example.invalid./", "id.example.invalid.")]
   [InlineData("https://[::1]:8443/x", "https://[::1]:8443/", "[::1]:8443")]
   [InlineData("https://[0:0:0:0:0:0:0:1]/", "https://[::1]/", "[::1]")]
   [InlineData("https://127.1:8443/", "https://127.0.0.1:8443/", "127.0.0.1:8443")]
   [InlineData("https://0x7f.1/", "https://127.0.0.1/", "127.0.0.1")]
   [InlineData("wss://dev.example.invalid/qqq/v1/qbit/printing/device/realtime", "wss://dev.example.invalid/", "dev.example.invalid")]
   [InlineData("http://127.0.0.1:8080/x", "http://127.0.0.1:8080/", "127.0.0.1:8080")]
   public void OriginIsSchemeAsciiHostAndPort(string address, string origin, string authority)
   {
      Uri found = Assert.IsType<Uri>(QqqOrigin.Of(new Uri(address)));
      Assert.Equal(origin, found.AbsoluteUri);
      Assert.Equal(authority, found.Authority);
      Assert.Equal(found, QqqOrigin.Of(found));
      Assert.All(found.AbsoluteUri, character => Assert.InRange(character, '!', '~'));
   }



   /// <summary>
   /// What is not an origin: no address, a relative one, one that is not an HTTP or WebSocket address (the browser
   /// callback's custom scheme, a mail address, any other scheme), and one whose host the platform cannot give an
   /// ASCII name. A line or paragraph separator in a host is such a case, so it can never reach a screen or a log as
   /// part of an origin; so is a name the platform does not treat as a domain name and leaves in Unicode.
   /// </summary>
   [Theory]
   [InlineData(null)]
   [InlineData("/qqq/v1/version")]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback")]
   [InlineData("mailto:operator@example.invalid")]
   [InlineData("custom://dev.example.invalid/x")]
   [InlineData("https://id\u2028.example.invalid")]
   [InlineData("https://id\u2029.example.invalid/application/")]
   [InlineData("https://-b\u00E4d-.example.invalid")]
   public void AddressWithoutAnAsciiHostIsNoOrigin(string? address)
   {
      Assert.Null(QqqOrigin.Of(address is null ? null : new Uri(address, UriKind.RelativeOrAbsolute)));
   }



   /// <summary>
   /// The session client holds a sign-in provider's endpoints to its issuer's origin, and every address in that
   /// comparison is text a server supplied. An address with no nameable origin is not the same origin as anything, so
   /// each of the four places it can appear is a refusal the caller can report (a configuration problem, or an
   /// invalid answer for the server's own sign-out data) and never the platform's exception for a host it cannot
   /// name: the issuer the server advertises, the provider's authorization endpoint, the end-session endpoint the
   /// server returns at sign-out, and the one in the provider's own discovery.
   /// </summary>
   [Theory]
   [InlineData("issuer", QqqSessionException.FailureKind.ConfigurationRequired)]
   [InlineData("authorization-endpoint", QqqSessionException.FailureKind.ConfigurationRequired)]
   [InlineData("logout-endpoint", QqqSessionException.FailureKind.InvalidResponse)]
   [InlineData("provider-end-session", QqqSessionException.FailureKind.ConfigurationRequired)]
   public async Task ProviderAddressWithoutANameableOriginIsARefusalNotAnException(string where, QqqSessionException.FailureKind failure)
   {
      const string Named = "https://id.example.invalid";
      string unnameable = "https://id" + (char)0x2028 + ".example.invalid";
      var callback = new Uri(ScriptedServer.Callback);
      bool issuer = string.Equals(where, "issuer", StringComparison.Ordinal);
      string provider = where switch
      {
         "issuer" => ScriptedServer.Provider.Replace("\"issuer\":\"" + Named, "\"issuer\":\"" + unnameable, StringComparison.Ordinal),
         "authorization-endpoint" => ScriptedServer.Provider.Replace(Named + "/authorize", unnameable + "/authorize", StringComparison.Ordinal),
         _ => ScriptedServer.Provider,
      };
      using var server = new ScriptedServer();
      server.Add(ScriptedServer.Discovery).Add(issuer ? ScriptedServer.OAuth.Replace("\"issuer\":\"" + Named, "\"issuer\":\"" + unnameable, StringComparison.Ordinal) : ScriptedServer.OAuth).Add(provider);
      using var client = new QqqSessionClient(ScriptedServer.Origin, server);
      if(where is "issuer" or "authorization-endpoint")
      {
         Assert.Equal(failure, (await Assert.ThrowsAsync<QqqSessionException>(() => client.BeginBrowserLoginAsync(callback))).Failure);
         return;
      }

      server.Add("{}", cookie: "sessionUUID=browser-session; Path=/; HttpOnly; Secure");
      QqqBrowserLogin attempt = await client.BeginBrowserLoginAsync(callback);
      string state = attempt.AuthorizationUri.Query[1..].Split('&').Single(pair => pair.StartsWith("state=", StringComparison.Ordinal))[6..];
      await client.CompleteBrowserLoginAsync(attempt, new Uri(ScriptedServer.Callback + "?code=synthetic-code&state=" + state));
      string endSession = (string.Equals(where, "logout-endpoint", StringComparison.Ordinal) ? unnameable : Named) + "/end-session";
      server.Add("{\"nativeLogoutData\":{\"issuer\":\"" + ScriptedServer.Issuer + "\",\"endSessionEndpoint\":\"" + endSession + "\",\"postLogoutRedirectUri\":\"" + ScriptedServer.Callback + "\",\"idTokenHint\":\"synthetic.id-token.hint\"}}");
      server.Add(string.Equals(where, "provider-end-session", StringComparison.Ordinal) ? ScriptedServer.Provider.Replace(Named + "/end-session", unnameable + "/end-session", StringComparison.Ordinal) : ScriptedServer.Provider);

      Assert.Equal(failure, (await Assert.ThrowsAsync<QqqSessionException>(() => client.SignOutWithProviderAsync(callback))).Failure);
      Assert.False(client.IsAuthenticated);
   }
}
