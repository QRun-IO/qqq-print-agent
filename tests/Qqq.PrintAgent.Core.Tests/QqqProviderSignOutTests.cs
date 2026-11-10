/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// Signing out of a browser-authenticated session should also end the identity provider's own session, but only through
/// an end-session request the client has checked: the issuer QQQ advertises, an HTTPS endpoint on that issuer's origin
/// that the provider's own discovery confirms, and the callback that was asked for. These tests cover the request that
/// is produced, every reason there is nothing to open, and every mismatch that must be refused; in all of them the
/// local session ends.
/// </summary>
public sealed class QqqProviderSignOutTests
{
   private const string LogoutData = """{"nativeLogoutData":{"issuer":"https://id.example.invalid/application/","endSessionEndpoint":"https://id.example.invalid/end-session","postLogoutRedirectUri":"io.qrun.qqq-print-agent:/oauth/callback","idTokenHint":"synthetic.id-token.hint"}}""";
   private static readonly Uri Redirect = new(ScriptedServer.Callback);



   /// <summary>
   /// With the capability advertised and the callback listed, logout asks the server for the provider's sign-out data and
   /// the client returns an end-session address carrying the ID token hint, the callback and a fresh state. The hint is in
   /// the address only: the log and the label never show it. The returned object recognises its own callback.
   /// </summary>
   [Fact]
   public async Task ProviderSignOutIsBuiltFromCheckedData()
   {
      var info = new List<string>();
      (QqqSessionClient client, ScriptedServer server) = await BrowserSessionAsync(info);
      using(client)
      {
         server.Add(LogoutData).Add(ScriptedServer.Provider);
         QqqProviderLogout logout = Assert.IsType<QqqProviderLogout>(await client.SignOutWithProviderAsync(Redirect));
         Assert.False(client.IsAuthenticated);
         ScriptedRequest request = server.Requests[^2];
         Assert.Equal("/qqq/v1/logout", request.Uri.AbsolutePath);
         using var body = JsonDocument.Parse(request.Body!);
         Assert.Equal(ScriptedServer.Callback, body.RootElement.GetProperty("nativeLogout").GetProperty("postLogoutRedirectUri").GetString());
         Assert.Contains("sessionUUID=browser-session", request.Cookie, StringComparison.Ordinal);
         Assert.True(server.Requests[^1].IsPublic);

         Assert.Equal("https://id.example.invalid/end-session", logout.EndSessionUri.GetLeftPart(UriPartial.Path));
         var query = logout.EndSessionUri.Query[1..].Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
         Assert.Equal("synthetic.id-token.hint", query["id_token_hint"]);
         Assert.Equal(ScriptedServer.Callback, query["post_logout_redirect_uri"]);
         Assert.Equal(logout.State, query["state"]);
         Assert.Equal(43, logout.State.Length);
         Assert.Equal(Redirect, logout.RedirectUri);
         Assert.Equal("QQQ provider sign-out (redacted)", logout.ToString());
         Assert.DoesNotContain(info, line => line.Contains("synthetic.id-token.hint", StringComparison.Ordinal));
         Assert.Contains("Provider sign-out ready: the end-session page is https://id.example.invalid/end-session (query not logged).", info, StringComparer.Ordinal);
         Assert.True(logout.Matches(new Uri(ScriptedServer.Callback + "?state=" + logout.State)));
      }
   }



   /// <summary>
   /// When there is nothing safe to open, the session is ended with the ordinary logout (an empty body, so the server is
   /// not asked for provider data), nothing is returned, and the log says the provider session was not ended and why.
   /// </summary>
   [Theory]
   [InlineData("capability", "the server does not advertise the nativeSessionLogout capability (UI API 1.10 or later)")]
   [InlineData("old-api", "the server does not advertise the nativeSessionLogout capability (UI API 1.10 or later)")]
   [InlineData("descriptor", "the server publishes no OIDC_RP_INITIATED_LOGOUT descriptor")]
   [InlineData("flow", "the server publishes no OIDC_RP_INITIATED_LOGOUT descriptor")]
   [InlineData("callback", "the server does not list io.qrun.qqq-print-agent:/oauth/callback as a post-logout callback")]
   public async Task WithoutAUsableProviderSignOutTheSessionEndsPlainly(string fault, string reason)
   {
      var info = new List<string>();
      string discovery = fault switch
      {
         "capability" => ScriptedServer.Discovery.Replace(",\"nativeSessionLogout\"", "", StringComparison.Ordinal),
         "old-api" => ScriptedServer.Discovery.Replace("1.10.0", "1.9.0", StringComparison.Ordinal),
         _ => ScriptedServer.Discovery,
      };
      string metadata = fault switch
      {
         "descriptor" => ScriptedServer.OAuth.Replace("\"nativeSessionLogout\"", "\"somethingElse\"", StringComparison.Ordinal),
         "flow" => ScriptedServer.OAuth.Replace("OIDC_RP_INITIATED_LOGOUT", "FRONT_CHANNEL", StringComparison.Ordinal),
         "callback" => ScriptedServer.OAuth.Replace("\"postLogoutRedirectUris\":[\"io.qrun.qqq-print-agent:/oauth/callback\"]", "\"postLogoutRedirectUris\":[\"io.qrun.other:/signed-out\"]", StringComparison.Ordinal),
         _ => ScriptedServer.OAuth,
      };
      (QqqSessionClient client, ScriptedServer server) = await BrowserSessionAsync(info, discovery, metadata);
      using(client)
      {
         server.Add("{}");
         Assert.Null(await client.SignOutWithProviderAsync(Redirect));
         Assert.False(client.IsAuthenticated);
         Assert.Equal("{}", server.Requests[^1].Body);
         Assert.Equal("/qqq/v1/logout", server.Requests[^1].Uri.AbsolutePath);
         Assert.Contains("The identity provider session was NOT ended: " + reason + ".", info, StringComparer.Ordinal);
      }
   }



   /// <summary>A password session has no identity provider behind it, and a client with no session has nothing to end: both return nothing, and the second makes no request at all.</summary>
   [Fact]
   public async Task PasswordSessionAndNoSessionHaveNothingToOpen()
   {
      var info = new List<string>();
      using ScriptedServer server = ScriptedServer.SignedIn().Add("{}");
      using var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: new QqqSessionDiagnostics(info.Add));
      Assert.Null(await client.SignOutWithProviderAsync(Redirect));
      Assert.Empty(server.Requests);
      Assert.Contains("Sign-out requested with no held session; clearing local state only.", info, StringComparer.Ordinal);

      await client.SignInWithPasswordAsync("synthetic-user", "synthetic-password");
      Assert.Null(await client.SignOutWithProviderAsync(Redirect));
      Assert.False(client.IsAuthenticated);
      Assert.Equal("{}", server.Requests[^1].Body);
      Assert.Contains("The identity provider session was NOT ended: this server signs in with a password, so there is no identity provider session.", info, StringComparer.Ordinal);
   }



   /// <summary>A server that offers provider sign-out but returns no data for this session (an older session, say) leaves nothing to open; the session still ends.</summary>
   [Fact]
   public async Task MissingSignOutDataLeavesNothingToOpen()
   {
      var info = new List<string>();
      (QqqSessionClient client, ScriptedServer server) = await BrowserSessionAsync(info);
      using(client)
      {
         server.Add("""{"nativeLogoutData":null}""");
         Assert.Null(await client.SignOutWithProviderAsync(Redirect));
         Assert.False(client.IsAuthenticated);
         Assert.Contains("The identity provider session was NOT ended: the server returned no provider sign-out data (an older session, or none was stored).", info, StringComparer.Ordinal);
      }
   }



   /// <summary>
   /// Sign-out data that does not match what was advertised is refused, never opened: another issuer, another callback, a
   /// missing, oversized or spaced ID token hint, an endpoint that is not HTTPS or not on the issuer's origin, an endpoint
   /// the provider's own discovery does not confirm, and a provider whose discovery names another issuer or no endpoint.
   /// The local session has ended in every case.
   /// </summary>
   [Theory]
   [InlineData("issuer", QqqSessionException.FailureKind.InvalidResponse)]
   [InlineData("callback", QqqSessionException.FailureKind.InvalidResponse)]
   [InlineData("hint-missing", QqqSessionException.FailureKind.InvalidResponse)]
   [InlineData("hint-spaced", QqqSessionException.FailureKind.InvalidResponse)]
   [InlineData("hint-oversized", QqqSessionException.FailureKind.InvalidResponse)]
   [InlineData("endpoint-plain", QqqSessionException.FailureKind.InvalidResponse)]
   [InlineData("endpoint-elsewhere", QqqSessionException.FailureKind.InvalidResponse)]
   [InlineData("endpoint-unconfirmed", QqqSessionException.FailureKind.InvalidResponse)]
   [InlineData("provider-issuer", QqqSessionException.FailureKind.ConfigurationRequired)]
   [InlineData("provider-no-endpoint", QqqSessionException.FailureKind.ConfigurationRequired)]
   public async Task MismatchedSignOutDataIsRefused(string fault, QqqSessionException.FailureKind failure)
   {
      var info = new List<string>();
      string data = fault switch
      {
         "issuer" => LogoutData.Replace("\"issuer\":\"https://id.example.invalid/application/\"", "\"issuer\":\"https://evil.invalid/\"", StringComparison.Ordinal),
         "callback" => LogoutData.Replace("io.qrun.qqq-print-agent:/oauth/callback", "io.qrun.other:/signed-out", StringComparison.Ordinal),
         "hint-missing" => LogoutData.Replace(",\"idTokenHint\":\"synthetic.id-token.hint\"", "", StringComparison.Ordinal),
         "hint-spaced" => LogoutData.Replace("synthetic.id-token.hint", "synthetic hint", StringComparison.Ordinal),
         "hint-oversized" => LogoutData.Replace("synthetic.id-token.hint", new string('h', 32_769), StringComparison.Ordinal),
         "endpoint-plain" => LogoutData.Replace("https://id.example.invalid/end-session", "http://id.example.invalid/end-session", StringComparison.Ordinal),
         "endpoint-elsewhere" => LogoutData.Replace("https://id.example.invalid/end-session", "https://evil.invalid/end-session", StringComparison.Ordinal),
         "endpoint-unconfirmed" => LogoutData.Replace("https://id.example.invalid/end-session", "https://id.example.invalid/other-end-session", StringComparison.Ordinal),
         _ => LogoutData,
      };
      string provider = fault switch
      {
         "provider-issuer" => ScriptedServer.Provider.Replace("\"issuer\":\"https://id.example.invalid/application/\"", "\"issuer\":\"https://evil.invalid/\"", StringComparison.Ordinal),
         "provider-no-endpoint" => ScriptedServer.Provider.Replace("\"end_session_endpoint\":\"https://id.example.invalid/end-session\",", "", StringComparison.Ordinal),
         _ => ScriptedServer.Provider,
      };
      (QqqSessionClient client, ScriptedServer server) = await BrowserSessionAsync(info);
      using(client)
      {
         server.Add(data).Add(provider);
         QqqSessionException refused = await Assert.ThrowsAsync<QqqSessionException>(() => client.SignOutWithProviderAsync(Redirect));
         Assert.Equal(failure, refused.Failure);
         Assert.False(client.IsAuthenticated);
         Assert.Contains(info, line => line.StartsWith("Provider sign-out refused: ", StringComparison.Ordinal));
         Assert.DoesNotContain(info, line => line.Contains("synthetic", StringComparison.Ordinal) && line.Contains("hint", StringComparison.Ordinal));
      }
   }



   /// <summary>
   /// The provider's return to the app counts as this sign-out finishing only on exactly the registered address with this
   /// sign-out's own state: a different state, a repeated state, a fragment, user information, another path, no query or a
   /// malformed pair is not it.
   /// </summary>
   [Theory]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?state=expected-state", true)]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?other=1&state=expected-state", true)]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?state=another-state", false)]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?state=expected-state&state=expected-state", false)]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?state=expected-state#fragment", false)]
   [InlineData("io.qrun.qqq-print-agent:/oauth/elsewhere?state=expected-state", false)]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback", false)]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?state", false)]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?other=1", false)]
   [InlineData("/oauth/callback?state=expected-state", false)]
   public void OnlyTheRegisteredReturnWithItsOwnStateMatches(string callback, bool matches)
   {
      var logout = new QqqProviderLogout(new Uri("https://id.example.invalid/end-session?id_token_hint=synthetic"), Redirect, "expected-state");
      Assert.Equal(matches, logout.Matches(new Uri(callback, UriKind.RelativeOrAbsolute)));
   }



   /// <summary>A sign-out cannot be built without its address, its callback and its state, and cannot be matched against nothing.</summary>
   [Fact]
   public void ProviderSignOutRequiresAllOfItsParts()
   {
      var address = new Uri("https://id.example.invalid/end-session");
      Assert.Throws<ArgumentNullException>(() => new QqqProviderLogout(null!, Redirect, "state"));
      Assert.Throws<ArgumentNullException>(() => new QqqProviderLogout(address, null!, "state"));
      Assert.Throws<ArgumentNullException>(() => new QqqProviderLogout(address, Redirect, null!));
      Assert.Throws<ArgumentNullException>(() => new QqqProviderLogout(address, Redirect, "state").Matches(null!));
   }



   /// <summary>A client holding a session obtained through the browser flow against an OAuth2 server, with every reply up to that point consumed.</summary>
   private static async Task<(QqqSessionClient Client, ScriptedServer Server)> BrowserSessionAsync(List<string> info, string discovery = ScriptedServer.Discovery, string metadata = ScriptedServer.OAuth)
   {
      var server = new ScriptedServer();
      server.Add(discovery).Add(metadata).Add(ScriptedServer.Provider).Add("{}", cookie: "sessionUUID=browser-session; Path=/; HttpOnly; Secure");
      var client = new QqqSessionClient(ScriptedServer.Origin, server, diagnostics: new QqqSessionDiagnostics(info.Add));
      QqqBrowserLogin attempt = await client.BeginBrowserLoginAsync(Redirect);
      string state = attempt.AuthorizationUri.Query[1..].Split('&').Single(pair => pair.StartsWith("state=", StringComparison.Ordinal))[6..];
      await client.CompleteBrowserLoginAsync(attempt, new Uri(ScriptedServer.Callback + "?code=synthetic-code&state=" + state));
      Assert.True(client.IsAuthenticated);
      return (client, server);
   }
}
