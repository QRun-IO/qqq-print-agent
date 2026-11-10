/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Signing out must also end the identity provider's browser session, because the provider is invisible to users and the next
/// sign-in must always ask for credentials. These tests script the QQQ server and the provider's discovery document to check that
/// every browser sign-in forces re-authentication, that sign-out returns a provider end-session request only when everything the server
/// said matches the provider's own discovery, and that the service hands that request to the companion without logging it.
/// </summary>
public sealed class ProviderSignOutTests
{
   private static readonly Uri Origin = new("https://printing.example.invalid");
   private static readonly Uri Redirect = new(QqqBrowserCallback.RedirectUri);
   private const string Issuer = "https://id.example.invalid/application/";
   private const string EndSession = "https://id.example.invalid/application/end-session/";
   private const string Features = "\"version\",\"metaData\",\"processes\",\"processCancel\",\"extensions\",\"widgets\",\"nativeSessionLogin\"";
   private const string Extension = "\"extensions\":[{\"name\":\"printing\",\"version\":\"1.0.0\",\"pathPrefix\":\"/qbit/printing\"}]";
   private const string Hint = "header.payload.signature";



   /// <summary>Builds the server's discovery document, with or without the provider sign-out capability.</summary>
   private static string Discovery(bool logout) => "{\"apiVersion\":\"" + (logout ? "1.10.0" : "1.9.0") + "\",\"features\":[" + Features + (logout ? ",\"nativeSessionLogout\"" : "") + "],\"clientChannels\":[\"api\"]," + Extension + "}";



   /// <summary>Builds the OAuth2 authentication description with the native sign-in grant and, optionally, the provider sign-out descriptor for the given callbacks.</summary>
   private static string OAuth(string? logoutCallbacks) => "{\"name\":\"p\",\"type\":\"OAUTH2\",\"values\":{\"clientId\":\"c\",\"scopes\":\"openid profile\",\"nativeSessionLogin\":{\"flow\":\"AUTHORIZATION_CODE_PKCE_SESSION\",\"codeChallengeMethod\":\"S256\",\"prompt\":\"login\",\"issuer\":\""
      + Issuer + "\",\"discoveryUrl\":\"" + Issuer + ".well-known/openid-configuration\",\"redirectUris\":[\"" + QqqBrowserCallback.RedirectUri + "\"]}"
      + (logoutCallbacks is null ? "" : ",\"nativeSessionLogout\":{\"flow\":\"OIDC_RP_INITIATED_LOGOUT\",\"postLogoutRedirectUris\":[" + logoutCallbacks + "]}") + "}}";



   /// <summary>Builds the provider's own discovery document.</summary>
   private static string Provider() => "{\"issuer\":\"" + Issuer + "\",\"authorization_endpoint\":\"https://id.example.invalid/authorize\",\"end_session_endpoint\":\"" + EndSession + "\",\"code_challenge_methods_supported\":[\"S256\"]}";



   /// <summary>Builds the logout reply carrying the provider's end-session data, with any field replaced to test a mismatch.</summary>
   private static string LogoutData(string? issuer = null, string? endpoint = null, string? callback = null, string? hint = null)
      => "{\"nativeLogoutData\":{\"issuer\":\"" + (issuer ?? Issuer) + "\",\"endSessionEndpoint\":\"" + (endpoint ?? EndSession) + "\",\"postLogoutRedirectUri\":\""
         + (callback ?? QqqBrowserCallback.RedirectUri) + "\",\"idTokenHint\":\"" + (hint ?? Hint) + "\"}}";



   /// <summary>Signs in through the scripted browser flow so the client holds a session, and returns it with the server that recorded the conversation.</summary>
   private static async Task<(QqqSessionClient Client, Server Server, QqqBrowserLogin Attempt)> SignedInAsync(bool logout, string? callbacks, params string[] afterLogin)
   {
      Server server = new();
      server.Add(Discovery(logout));
      server.Add(OAuth(callbacks));
      server.Add(Provider());
      server.Add("{}", "sessionUUID=opaque-session; Path=/; HttpOnly; Secure");
      foreach(string reply in afterLogin)
      { server.Add(reply); }
      QqqSessionClient client = new(Origin, server);
      QqqBrowserLogin attempt = await client.BeginBrowserLoginAsync(Redirect);
      string state = Query(attempt.AuthorizationUri)["state"];
      await client.CompleteBrowserLoginAsync(attempt, new Uri(Redirect.AbsoluteUri + "?code=one-time-code&state=" + Uri.EscapeDataString(state)));
      Assert.True(client.IsAuthenticated);
      return (client, server, attempt);
   }



   /// <summary>Every browser sign-in asks the provider to re-authenticate: prompt=login and max_age=0, because prompt=login alone was seen reusing a retained provider session.</summary>
   [Fact]
   public async Task BrowserSignInForcesFreshAuthentication()
   {
      (QqqSessionClient client, Server _, QqqBrowserLogin attempt) = await SignedInAsync(logout: true, callbacks: null);
      using(client)
      {
         Dictionary<string, string> query = Query(attempt.AuthorizationUri);
         Assert.Equal("login", query["prompt"]);
         Assert.Equal("0", query["max_age"]);
      }
   }



   /// <summary>
   /// With a server that offers provider sign-out, the logout request names only the registered callback and carries only the session
   /// cookie; the returned end-session request is built for the provider's own endpoint with the hint, callback and a fresh state; the
   /// local session is gone; and only a return with that state counts as the provider finishing.
   /// </summary>
   [Fact]
   public async Task SignOutReturnsTheProviderEndSessionRequestAndForgetsTheSession()
   {
      (QqqSessionClient client, Server server, QqqBrowserLogin _) = await SignedInAsync(true, "\"" + QqqBrowserCallback.RedirectUri + "\"", LogoutData(), Provider());
      using(client)
      {
         QqqProviderLogout? logout = await client.SignOutWithProviderAsync(Redirect);
         Assert.False(client.IsAuthenticated);
         Assert.NotNull(logout);
         Request request = server.Requests[4];
         Assert.Equal("/qqq/v1/logout", request.Uri.AbsolutePath);
         Assert.Null(request.Authorization);
         Assert.Contains("sessionUUID=opaque-session", request.Cookie, StringComparison.Ordinal);
         using var body = JsonDocument.Parse(request.Body!);
         Assert.Equal(QqqBrowserCallback.RedirectUri, body.RootElement.GetProperty("nativeLogout").GetProperty("postLogoutRedirectUri").GetString());
         Assert.Single(body.RootElement.EnumerateObject());
         Assert.Equal(EndSession, logout.EndSessionUri.GetLeftPart(UriPartial.Path));
         Dictionary<string, string> query = Query(logout.EndSessionUri);
         Assert.Equal(Hint, query["id_token_hint"]);
         Assert.Equal(QqqBrowserCallback.RedirectUri, query["post_logout_redirect_uri"]);
         Assert.Equal(43, query["state"].Length);
         Assert.Equal("QQQ provider sign-out (redacted)", logout.ToString());
         Assert.True(logout.Matches(new Uri(Redirect.AbsoluteUri + "?state=" + Uri.EscapeDataString(logout.State))));
         Assert.False(logout.Matches(new Uri(Redirect.AbsoluteUri + "?state=other")));
         Assert.False(logout.Matches(new Uri(Redirect.AbsoluteUri + "?code=x")));
         Assert.False(logout.Matches(new Uri("io.qrun.other:/oauth/callback?state=" + Uri.EscapeDataString(logout.State))));
      }
   }



   /// <summary>A server without the capability (or without this callback listed) still gets an ordinary logout that ends the QQQ session, and nothing is returned to open.</summary>
   [Theory]
   [InlineData(false, "\"io.qrun.qqq-print-agent:/oauth/callback\"")]
   [InlineData(true, null)]
   [InlineData(true, "\"io.qrun.other:/oauth/callback\"")]
   public async Task ServerWithoutProviderSignOutGetsOrdinaryLogout(bool capability, string? callbacks)
   {
      (QqqSessionClient client, Server server, QqqBrowserLogin _) = await SignedInAsync(capability, callbacks, "{}");
      using(client)
      {
         Assert.Null(await client.SignOutWithProviderAsync(Redirect));
         Assert.False(client.IsAuthenticated);
         Assert.Equal("{}", server.Requests[4].Body);
         Assert.Equal(5, server.Requests.Count);
      }
   }



   /// <summary>
   /// Provider data that does not match what the server advertised, or what the provider's own discovery publishes, is refused with no
   /// request returned, and the local session is still forgotten.
   /// </summary>
   [Theory]
   [InlineData("issuer")]
   [InlineData("callback")]
   [InlineData("hint-space")]
   [InlineData("hint-empty")]
   [InlineData("endpoint-http")]
   [InlineData("endpoint-other-origin")]
   [InlineData("endpoint-differs-from-discovery")]
   public async Task MismatchedProviderDataIsRefusedAndSessionStillCleared(string flaw)
   {
      string data = flaw switch
      {
         "issuer" => LogoutData(issuer: "https://evil.example.invalid/application/"),
         "callback" => LogoutData(callback: "io.qrun.other:/oauth/callback"),
         "hint-space" => LogoutData(hint: "a b"),
         "hint-empty" => LogoutData(hint: ""),
         "endpoint-http" => LogoutData(endpoint: "http://id.example.invalid/application/end-session/"),
         "endpoint-other-origin" => LogoutData(endpoint: "https://evil.example.invalid/end-session/"),
         _ => LogoutData(endpoint: "https://id.example.invalid/application/other-end-session/"),
      };
      (QqqSessionClient client, Server _, QqqBrowserLogin _) = await SignedInAsync(true, "\"" + QqqBrowserCallback.RedirectUri + "\"", data, Provider());
      using(client)
      {
         _ = await Assert.ThrowsAsync<QqqSessionException>(() => client.SignOutWithProviderAsync(Redirect));
         Assert.False(client.IsAuthenticated);
      }
   }



   /// <summary>A server that returns no provider data (an older session) ends the QQQ session and returns nothing to open.</summary>
   [Fact]
   public async Task MissingProviderDataReturnsNothingToOpen()
   {
      (QqqSessionClient client, Server _, QqqBrowserLogin _) = await SignedInAsync(true, "\"" + QqqBrowserCallback.RedirectUri + "\"", "{}");
      using(client)
      {
         Assert.Null(await client.SignOutWithProviderAsync(Redirect));
         Assert.False(client.IsAuthenticated);
      }
   }



   /// <summary>The companion's sign-out reaches the service with the registered callback, and the reply carries the provider request for the browser; the command line sends no callback and gets plain text.</summary>
   [Fact]
   public async Task ServiceReturnsTheProviderRequestToTheCompanionOnly()
   {
      using SessionFixture fixture = new();
      QqqProviderLogout logout = new(new Uri(EndSession + "?id_token_hint=" + Hint + "&state=s"), Redirect, "s");
      fixture.Session.ProviderLogout = logout;
      using SingleServerHarness runtime = fixture.Runtime();
      _ = await runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: fixture.Session.ExportCredential(), ClaimLegacyDevice: true), CancellationToken.None);
      string reply = await runtime.SignOutAsync(QqqBrowserCallback.RedirectUri, CancellationToken.None);
      PipeSignOutResult result = JsonSerializer.Deserialize<PipeSignOutResult>(reply, PrintApiClient.JsonOptions)!;
      Assert.Equal(Redirect, fixture.Session.RequestedLogoutRedirect);
      Assert.Equal(logout.EndSessionUri, result.ProviderLogout!.EndSessionUri);
      Assert.Equal("s", result.ProviderLogout.State);
      Assert.StartsWith("Signed out.", result.Message, StringComparison.Ordinal);

      _ = await runtime.SignInAsync(new PipeRequest("sign-in", SessionCredential: fixture.Session.ExportCredential(), ClaimLegacyDevice: true), CancellationToken.None);
      string plain = await runtime.SignOutAsync(CancellationToken.None);
      Assert.StartsWith("Signed out.", plain, StringComparison.Ordinal);
      Assert.Equal(Redirect, fixture.Session.RequestedLogoutRedirect);
   }



   /// <summary>The service accepts only this application's registered callback, so a local client cannot steer the provider's return elsewhere.</summary>
   [Fact]
   public async Task ServiceRefusesAnyOtherCallback()
   {
      using SessionFixture fixture = new();
      using SingleServerHarness runtime = fixture.Runtime();
      _ = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SignOutAsync("https://evil.example.invalid/cb", CancellationToken.None));
   }



   /// <summary>Splits a URL's query string into decoded name and value pairs.</summary>
   private static Dictionary<string, string> Query(Uri uri) => uri.Query[1..].Split('&').Select(part => part.Split('=', 2))
      .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]), StringComparer.Ordinal);



   /// <summary>What the scripted server saw for one request.</summary>
   private sealed record Request(Uri Uri, string? Body, string? Authorization, string? Cookie);



   /// <summary>Scripted server: records each request and answers in the order replies were queued, so a test fixes the whole conversation.</summary>
   private sealed class Server : HttpMessageHandler
   {
      private readonly Queue<HttpResponseMessage> _replies = new();
      internal List<Request> Requests { get; } = [];



      /// <summary>Queues a JSON reply, optionally with a Set-Cookie header.</summary>
      internal void Add(string body, string? cookie = null)
      {
         var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
         if(cookie is not null)
         { response.Headers.Add("Set-Cookie", cookie); }
         _replies.Enqueue(response);
      }



      /// <summary>Records the request and returns the next queued reply.</summary>
      protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
         Requests.Add(new Request(request.RequestUri!, body, request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("Cookie", out IEnumerable<string>? cookie) ? string.Join("; ", cookie) : null));
         return _replies.Dequeue();
      }
   }
}
