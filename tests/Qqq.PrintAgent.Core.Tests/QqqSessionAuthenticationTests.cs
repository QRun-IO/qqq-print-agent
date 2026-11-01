/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// The sign-in client's trust rules, tested against scripted server responses: compatibility is checked before any credential is
/// sent, a password goes only to the standard session endpoint, the browser flow uses PKCE with strict callback validation, and
/// device proof headers travel only on routes the server scopes to the signed-in device.
/// </summary>
public sealed class QqqSessionAuthenticationTests
{
   private static readonly Uri Origin = new("https://printing.example.invalid");
   private const string Discovery = """{"apiVersion":"1.9.0","features":["version","metaData","processes","processCancel","extensions","widgets","nativeSessionLogin"],"clientChannels":["api"],"extensions":[{"name":"printing","version":"1.0.0","pathPrefix":"/qbit/printing"}]}""";
   private const string Table = """{"name":"local","type":"TABLE_BASED"}""";
   private const string Identity = """{"principalId":"user-1","tenantId":"tenant-1","deviceId":"device-1"}""";
   private const string OAuth = """{"name":"configured-provider","type":"OAUTH2","values":{"clientId":"qqq-client","scopes":"openid profile offline_access","nativeClientId":"not-the-session-client","nativeSessionLogin":{"flow":"AUTHORIZATION_CODE_PKCE_SESSION","codeChallengeMethod":"S256","prompt":"login","issuer":"https://id.example.invalid/application/","discoveryUrl":"https://id.example.invalid/application/.well-known/openid-configuration","redirectUris":["io.qrun.qqq-print-agent:/oauth/callback"]}}}""";
   private const string Provider = """{"issuer":"https://id.example.invalid/application/","authorization_endpoint":"https://id.example.invalid/authorize","code_challenge_methods_supported":["S256"]}""";
   private static readonly Uri Redirect = new("io.qrun.qqq-print-agent:/oauth/callback");



   /// <summary>
   /// A server whose discovery document has the wrong API version, client channel, extension name or version, path prefix or a
   /// missing widgets feature is refused with a configuration error after exactly one public request, so no password, cookie or
   /// device header is ever sent to an incompatible or unexpected server.
   /// </summary>
   [Theory]
   [InlineData("1.9.0", "1.7.0")]
   [InlineData("1.9.0", "2.0.0")]
   [InlineData("1.9.0", "1.09.0")]
   [InlineData("\"api\"", "\"windows\"")]
   [InlineData("\"printing\"", "\"other\"")]
   [InlineData("\"1.0.0\"", "\"3.0.0\"")]
   [InlineData("/qbit/printing", "/other")]
   [InlineData("\"widgets\"", "\"other\"")]
   public async Task DiscoveryFailurePrecedesAllCredentials(string oldValue, string newValue)
   {
      using var handler = new Handler();
      handler.Add(Discovery.Replace(oldValue, newValue, StringComparison.Ordinal));
      using var client = new QqqSessionClient(Origin, handler);
      QqqSessionException exception = await Assert.ThrowsAsync<QqqSessionException>(() => client.SignInWithPasswordAsync("user", "secret"));
      Assert.Equal(QqqSessionException.FailureKind.ConfigurationRequired, exception.Failure);
      Request request = Assert.Single(handler.Requests);
      Assert.Equal("/qqq/v1/version", request.Uri.AbsolutePath);
      AssertPublic(request);
   }



   /// <summary>
   /// The password appears only as Basic credentials on the manageSession login and is not kept afterwards. A cookie-only response
   /// completes sign-in, later calls carry the cookie but no Authorization header, the exported credential redacts itself when
   /// printed, and logout is sent cookie-only with the server's own origin.
   /// </summary>
   [Fact]
   public async Task PasswordIsUsedOnlyForManageSessionAndCookieOnlyResponseIsAccepted()
   {
      using Handler handler = TableHandler();
      handler.Add("{}", cookie: "sessionUUID=session-secret; Path=/; Secure; HttpOnly");
      handler.Add(Identity);
      handler.Add("{}");
      using var client = new QqqSessionClient(Origin, handler);
      await client.SignInWithPasswordAsync("user", "password-not-retained");
      AssertPublic(handler.Requests[0]);
      AssertPublic(handler.Requests[1]);
      Request login = handler.Requests[2];
      Assert.Equal("/qqq/v1/manageSession", login.Uri.AbsolutePath);
      Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:password-not-retained")), login.Authorization);
      Assert.Null(login.Cookie);
      QqqSessionBinding binding = await client.GetSessionIdentityAsync();
      Assert.Equal("user-1", binding.PrincipalId);
      Assert.Null(handler.Requests[3].Authorization);
      Assert.Contains("sessionUUID=session-secret", handler.Requests[3].Cookie, StringComparison.Ordinal);
      QqqSessionCredential credential = client.ExportCredential();
      Assert.Equal("QQQ session credential (redacted)", credential.ToString());
      Assert.Equal("session-secret", credential.SessionToken);
      await client.SignOutAsync();
      Assert.False(client.IsAuthenticated);
      Assert.Equal("/qqq/v1/logout", handler.Requests[4].Uri.AbsolutePath);
      Assert.Null(handler.Requests[4].Authorization);
      Assert.Equal(Origin.GetLeftPart(UriPartial.Authority), handler.Requests[4].Origin);
   }



   /// <summary>
   /// Login never succeeds on ambiguous evidence: a body uuid that disagrees with the cookie, duplicate or wrong-typed uuid
   /// fields, no session at all, a cookie scoped to another path, or an immediately expired cookie. Any of these must leave the
   /// client unauthenticated rather than guessing which value is the real session.
   /// </summary>
   [Theory]
   [InlineData("""{"uuid":"other"}""", "sessionUUID=session-secret; Path=/; HttpOnly")]
   [InlineData("""{"uuid":"one","UUID":"two"}""", null)]
   [InlineData("{}", null)]
   [InlineData("""{"uuid":10}""", null)]
   [InlineData("null", "sessionUUID=session-secret; Path=/")]
   [InlineData("{}", "sessionUUID=secret; Path=/elsewhere")]
   [InlineData("{}", "sessionUUID=; Path=/; Max-Age=0")]
   public async Task AmbiguousOrInvalidSessionNeverAuthenticates(string body, string? cookie)
   {
      using Handler handler = TableHandler();
      handler.Add(body, cookie: cookie);
      using var client = new QqqSessionClient(Origin, handler);
      await Assert.ThrowsAsync<QqqSessionException>(() => client.SignInWithPasswordAsync("user", "password"));
      Assert.False(client.IsAuthenticated);
   }



   /// <summary>Two different session cookie names in one response (sessionUUID and sessionId) are contradictory, so login is rejected instead of choosing one.</summary>
   [Fact]
   public async Task ConflictingSessionCookiesAreRejected()
   {
      using Handler handler = TableHandler();
      handler.AddResponse(() =>
      {
         HttpResponseMessage response = Response("{}");
         response.Headers.Add("Set-Cookie", new[] { "sessionUUID=one; Path=/; HttpOnly", "sessionId=two; Path=/; HttpOnly" });
         return response;
      });
      using var client = new QqqSessionClient(Origin, handler);
      await Assert.ThrowsAsync<QqqSessionException>(() => client.SignInWithPasswordAsync("user", "password"));
      Assert.False(client.IsAuthenticated);
   }



   /// <summary>When the server advertises an authentication type the client does not implement, password sign-in stops after discovery and metadata, with no credential request sent.</summary>
   [Theory]
   [InlineData("TOKEN")]
   [InlineData("unknown")]
   [InlineData("")]
   public async Task UnknownProviderNeverReceivesPassword(string type)
   {
      using var handler = new Handler();
      handler.Add(Discovery);
      handler.Add(JsonSerializer.Serialize(new { type }));
      using var client = new QqqSessionClient(Origin, handler);
      await Assert.ThrowsAsync<QqqSessionException>(() => client.SignInWithPasswordAsync("user", "password"));
      Assert.Equal(2, handler.Requests.Count);
      Assert.All(handler.Requests, AssertPublic);
   }



   /// <summary>
   /// Restoring a saved session first rechecks discovery and authentication metadata with public requests, then resumes only
   /// through the standard manageSession call (an empty body, the cookie, the server origin, no Authorization header).
   /// </summary>
   [Fact]
   public async Task RestoreDiscoversBeforeCookieAndResumesOnlyThroughStandardManageSession()
   {
      using Handler handler = TableHandler();
      handler.Add("{}");
      handler.Add("{}");
      using var client = new QqqSessionClient(Origin, handler);
      await client.RestoreSessionAsync(new QqqSessionCredential(Origin, "saved-session"));
      await client.ResumeSessionAsync();
      AssertPublic(handler.Requests[0]);
      AssertPublic(handler.Requests[1]);
      Assert.All(handler.Requests.Skip(2), request =>
      {
         Assert.Equal("/qqq/v1/manageSession", request.Uri.AbsolutePath);
         Assert.Equal("{}", request.Body);
         Assert.Null(request.Authorization);
         Assert.Contains("sessionUUID=saved-session", request.Cookie, StringComparison.Ordinal);
         Assert.Equal(Origin.GetLeftPart(UriPartial.Authority), request.Origin);
      });
      Assert.Equal("saved-session", client.ExportCredential().SessionToken);
   }



   /// <summary>A saved credential from a different server is refused locally, before any request, so a session cookie can never be replayed to another host.</summary>
   [Fact]
   public async Task AnotherOriginCredentialIsRejectedWithoutNetwork()
   {
      using var handler = new Handler();
      using var client = new QqqSessionClient(Origin, handler);
      await Assert.ThrowsAsync<ArgumentException>(() => client.RestoreSessionAsync(new QqqSessionCredential(new Uri("https://another.invalid"), "secret")));
      Assert.Empty(handler.Requests);
   }



   /// <summary>
   /// After the server confirms the device belongs to the signed-in user, the device id and proof ride along only on device
   /// routes. The admin route and the enrollment route get the session cookie but never the device proof, so management calls
   /// cannot leak the device secret.
   /// </summary>
   [Fact]
   public async Task DeviceProofIsBoundByServerIdentityAndNeverSentToManagementOrEnrollment()
   {
      using Handler handler = TableHandler();
      handler.Add("""{"uuid":"session"}""");
      handler.Add("{}");
      handler.Add(Identity);
      handler.Add("{}");
      handler.Add("{}");
      handler.Add("{}");
      using var client = new QqqSessionClient(Origin, handler);
      await client.SignInWithPasswordAsync("user", "password");
      var proof = new QqqDeviceCredential("device-1", "device-secret");
      QqqSessionBinding binding = await client.BindDeviceAsync(proof, claimLegacyDevice: true);
      Assert.True(binding.HasApprovedDevice);
      using HttpResponseMessage device = await client.SendPrintingAsync(HttpMethod.Post, "device/control/poll");
      using HttpResponseMessage admin = await client.SendPrintingAsync(HttpMethod.Get, "admin/devices?cursor=a%2Fb");
      using HttpResponseMessage enroll = await client.SendPrintingAsync(HttpMethod.Post, "devices/enroll");
      Assert.Equal("/qqq/v1/qbit/printing/device/bind", handler.Requests[3].Uri.AbsolutePath);
      Assert.Equal("{}", handler.Requests[3].Body);
      Assert.All(handler.Requests.Skip(3).Take(3), request =>
      {
         Assert.Equal("device-1", request.DeviceId);
         Assert.Equal("device-secret", request.DeviceToken);
         Assert.Null(request.Authorization);
         Assert.NotNull(request.Cookie);
      });
      Assert.Null(handler.Requests[6].DeviceToken);
      Assert.Null(handler.Requests[7].DeviceToken);
      Assert.Equal("QQQ device credential (redacted)", proof.ToString());
   }



   /// <summary>If binding returns a different principal than sign-in did, the bind fails and the client signs out, even when the email address is identical; identity comes from the server's principal id, never from email.</summary>
   [Fact]
   public async Task BindingRejectsChangedPrincipalWithoutInferringIdentityFromEmail()
   {
      using Handler handler = TableHandler();
      handler.Add("""{"uuid":"session"}""");
      handler.Add("""{"principalId":"user-1","tenantId":"tenant-1","email":"same@example.invalid"}""");
      handler.Add("""{"principalId":"user-2","tenantId":"tenant-1","deviceId":"device-1","email":"same@example.invalid"}""");
      using var client = new QqqSessionClient(Origin, handler);
      await client.SignInWithPasswordAsync("user", "password");
      await client.GetSessionIdentityAsync();
      await Assert.ThrowsAsync<QqqSessionException>(() => client.BindDeviceAsync(new QqqDeviceCredential("device-1", "proof")));
      Assert.False(client.IsAuthenticated);
   }



   /// <summary>An identity reply lacking a principal and tenant id cannot approve a device, even if it names one; later device requests are refused without being sent.</summary>
   [Fact]
   public async Task IncompleteIdentityCannotApproveDevice()
   {
      using Handler handler = TableHandler();
      handler.Add("""{"uuid":"session"}""");
      handler.Add("""{"email":"user@example.invalid","deviceId":"device-1"}""");
      using var client = new QqqSessionClient(Origin, handler);
      await client.SignInWithPasswordAsync("user", "password");
      await Assert.ThrowsAsync<QqqSessionException>(() => client.BindDeviceAsync(new QqqDeviceCredential("device-1", "proof")));
      await Assert.ThrowsAsync<QqqSessionException>(() => client.SendPrintingAsync(HttpMethod.Get, "device/jobs"));
      Assert.Equal(4, handler.Requests.Count);
   }



   /// <summary>The client accepts the identity responses from the shared server-side fixtures (unbound and bound), so client and server agree on the wire shape.</summary>
   [Theory]
   [InlineData("printing-session.response.json", false)]
   [InlineData("printing-session-bound.response.json", true)]
   public async Task SharedAuthoritativeIdentityFixturesAreAccepted(string fixture, bool bound)
   {
      string body = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", fixture));
      using Handler handler = TableHandler();
      handler.Add("""{"uuid":"session"}""");
      handler.Add(body);
      using var client = new QqqSessionClient(Origin, handler);
      await client.SignInWithPasswordAsync("user", "password");
      QqqSessionBinding identity = bound ? await client.BindDeviceAsync(new QqqDeviceCredential("test-device", "proof"))
         : await client.GetSessionIdentityAsync();
      Assert.Equal("test-operator", identity.PrincipalId);
      Assert.Equal("test-tenant", identity.TenantId);
      Assert.Equal(bound, identity.HasApprovedDevice);
   }



   /// <summary>
   /// Printing calls must be a relative path inside the printing area. Absolute URLs, leading slashes, dot segments (plain or
   /// encoded), fragments and non-printing paths are rejected before any request, so credentials cannot be steered to another
   /// host or route.
   /// </summary>
   [Theory]
   [InlineData("https://elsewhere.invalid/device/jobs")]
   [InlineData("/device/jobs")]
   [InlineData("device/../admin/jobs")]
   [InlineData("device/%2e%2e/admin/jobs")]
   [InlineData("device/jobs#fragment")]
   [InlineData("assets/logo.png")]
   public async Task CredentialTransportRejectsUnscopedRoutesWithoutNetwork(string path)
   {
      using Handler handler = TableHandler();
      handler.Add("""{"uuid":"session"}""");
      using var client = new QqqSessionClient(Origin, handler);
      await client.SignInWithPasswordAsync("user", "password");
      await Assert.ThrowsAsync<ArgumentException>(() => client.SendPrintingAsync(HttpMethod.Get, path));
      Assert.Equal(3, handler.Requests.Count);
   }



   /// <summary>
   /// 401 means sign in again and drops the session; 403 means signed in but not permitted and keeps it. The distinction decides
   /// whether the operator is asked to log in or told they lack access.
   /// </summary>
   [Theory]
   [InlineData(HttpStatusCode.Unauthorized, QqqSessionException.FailureKind.AuthenticationRequired, false)]
   [InlineData(HttpStatusCode.Forbidden, QqqSessionException.FailureKind.PermissionDenied, true)]
   public async Task StandardAuthorizationFailuresAreDistinct(HttpStatusCode status, QqqSessionException.FailureKind failure, bool authenticated)
   {
      using Handler handler = TableHandler();
      handler.Add("""{"uuid":"session"}""");
      handler.Add("{}", status);
      using var client = new QqqSessionClient(Origin, handler);
      await client.SignInWithPasswordAsync("user", "password");
      QqqSessionException exception = await Assert.ThrowsAsync<QqqSessionException>(() => client.SendPrintingAsync(HttpMethod.Get, "admin/devices"));
      Assert.Equal(failure, exception.Failure);
      Assert.Equal(authenticated, client.IsAuthenticated);
   }



   /// <summary>A response that expires the session cookie ends the local session and reports authentication required, matching what the server just did.</summary>
   [Fact]
   public async Task ExpiredCookieOnApiResponseClearsSession()
   {
      using Handler handler = TableHandler();
      handler.Add("""{"uuid":"session"}""");
      handler.Add("{}", cookie: "sessionUUID=; Path=/; Max-Age=0");
      using var client = new QqqSessionClient(Origin, handler);
      await client.SignInWithPasswordAsync("user", "password");
      QqqSessionException exception = await Assert.ThrowsAsync<QqqSessionException>(() => client.SendPrintingAsync(HttpMethod.Get, "admin/devices"));
      Assert.Equal(QqqSessionException.FailureKind.AuthenticationRequired, exception.Failure);
      Assert.False(client.IsAuthenticated);
   }



   /// <summary>A redirect to the login call is a transport refusal: it is not followed, its cookie is not adopted, and no extra request is made, so credentials are never re-sent to a redirect target.</summary>
   [Fact]
   public async Task RedirectedLoginNeverFollowsOrAcceptsCredentialResponse()
   {
      using Handler handler = TableHandler();
      handler.Add("{}", HttpStatusCode.Redirect, cookie: "sessionUUID=not-adopted; Path=/");
      using var client = new QqqSessionClient(Origin, handler);
      QqqSessionException exception = await Assert.ThrowsAsync<QqqSessionException>(() => client.SignInWithPasswordAsync("user", "password"));
      Assert.Equal(QqqSessionException.FailureKind.TransportRefused, exception.Failure);
      Assert.False(client.IsAuthenticated);
      Assert.Equal(3, handler.Requests.Count);
   }



   /// <summary>
   /// Browser sign-in requires the server to advertise exactly the supported native grant: the PKCE session flow, S256 challenge,
   /// forced login prompt, the nativeSessionLogin block and an https issuer. Any weaker variant fails after two public requests,
   /// before the provider or any credential is contacted.
   /// </summary>
   [Theory]
   [InlineData("AUTHORIZATION_CODE_PKCE_SESSION", "AUTHORIZATION_CODE")]
   [InlineData("S256", "plain")]
   [InlineData("\"login\"", "\"none\"")]
   [InlineData("nativeSessionLogin", "nativeBearer")]
   [InlineData("https://id.example.invalid/application/", "http://id.example.invalid/application/")]
   public async Task UnsupportedNativeGrantFailsBeforeProviderOrCredentialRequest(string oldValue, string newValue)
   {
      using var handler = new Handler();
      handler.Add(Discovery);
      handler.Add(OAuth.Replace(oldValue, newValue, StringComparison.Ordinal));
      using var client = new QqqSessionClient(Origin, handler);
      QqqSessionException exception = await Assert.ThrowsAsync<QqqSessionException>(() => client.BeginBrowserLoginAsync(Redirect));
      Assert.Equal(QqqSessionException.FailureKind.ConfigurationRequired, exception.Failure);
      Assert.Equal(2, handler.Requests.Count);
      Assert.All(handler.Requests, AssertPublic);
   }



   /// <summary>Native session login needs API 1.9 and the explicit nativeSessionLogin feature; an older API or a missing feature is refused after discovery and metadata only.</summary>
   [Fact]
   public async Task NativeSessionRequiresCore19AndExplicitFeature()
   {
      foreach(string discovery in new[] { Discovery.Replace("1.9.0", "1.8.0", StringComparison.Ordinal), Discovery.Replace("nativeSessionLogin", "other", StringComparison.Ordinal) })
      {
         using var handler = new Handler();
         handler.Add(discovery);
         handler.Add(OAuth);
         using var client = new QqqSessionClient(Origin, handler);
         await Assert.ThrowsAsync<QqqSessionException>(() => client.BeginBrowserLoginAsync(Redirect));
         Assert.Equal(2, handler.Requests.Count);
      }
   }



   /// <summary>The login redirect must be the one the server advertised and the client supports; a different custom scheme or a loopback http address is refused before the provider is contacted.</summary>
   [Fact]
   public async Task OnlyExactAdvertisedAndCentralSupportedRedirectCanBeUsed()
   {
      foreach(Uri redirect in new[] { new Uri("io.qrun.qqq.print://callback/other"), new Uri("http://127.0.0.1:49152/callback") })
      {
         using var handler = new Handler();
         handler.Add(Discovery);
         handler.Add(OAuth);
         using var client = new QqqSessionClient(Origin, handler);
         await Assert.ThrowsAsync<QqqSessionException>(() => client.BeginBrowserLoginAsync(redirect));
         Assert.Equal(2, handler.Requests.Count);
      }
   }



   /// <summary>
   /// The authorization URL carries the client id, an S256 challenge, a forced login prompt, the exact redirect and 43-character
   /// state and nonce. Completing the callback sends only the one-time code, the flow name and the PKCE verifier (whose hash is the
   /// challenge) to manageSession with no credentials of any other kind, and the same attempt cannot be completed twice.
   /// </summary>
   [Fact]
   public async Task BrowserUsesPkceStatePromptAndOnlyStandardSessionExchange()
   {
      using Handler handler = OAuthHandler();
      handler.Add("{}", cookie: "sessionUUID=opaque-session; Path=/; HttpOnly; Secure");
      using var client = new QqqSessionClient(Origin, handler);
      QqqBrowserLogin attempt = await client.BeginBrowserLoginAsync(Redirect);
      Dictionary<string, string> authorization = Query(attempt.AuthorizationUri);
      Assert.Equal("qqq-client", authorization["client_id"]);
      Assert.Equal("S256", authorization["code_challenge_method"]);
      Assert.Equal("login", authorization["prompt"]);
      Assert.Equal(Redirect.AbsoluteUri, authorization["redirect_uri"]);
      Assert.Equal(43, authorization["state"].Length);
      Assert.Equal(43, authorization["nonce"].Length);
      Assert.Equal("QQQ browser sign-in attempt (redacted)", attempt.ToString());
      Assert.All(handler.Requests, AssertPublic);
      Assert.Null(handler.Requests[2].ClientChannel);
      await client.CompleteBrowserLoginAsync(attempt, Callback(attempt, authorization["state"]));
      Request exchange = handler.Requests[3];
      Assert.Equal("/qqq/v1/manageSession", exchange.Uri.AbsolutePath);
      Assert.Null(exchange.Authorization);
      Assert.Null(exchange.Cookie);
      Assert.Null(exchange.DeviceToken);
      using var body = JsonDocument.Parse(exchange.Body!);
      Assert.Equal(4, body.RootElement.EnumerateObject().Count());
      Assert.Equal("one-time-code", body.RootElement.GetProperty("code").GetString());
      Assert.Equal(QqqAuthenticationMetadata.NativeSessionFlow, body.RootElement.GetProperty("authenticationFlow").GetString());
      string verifier = body.RootElement.GetProperty("codeVerifier").GetString()!;
      Assert.Equal(authorization["code_challenge"], Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
      Assert.Equal("opaque-session", client.ExportCredential().SessionToken);
      await Assert.ThrowsAsync<QqqSessionException>(() => client.CompleteBrowserLoginAsync(attempt, Callback(attempt, authorization["state"])));
      Assert.Equal(4, handler.Requests.Count);
   }



   /// <summary>
   /// A callback with the wrong or repeated state, a different address, a fragment, a foreign issuer, or one arriving after the
   /// five-minute window is rejected without any exchange request, and it uses up the attempt so the correct callback cannot be
   /// replayed afterwards.
   /// </summary>
   [Theory]
   [InlineData("wrong-state")]
   [InlineData("duplicate-state")]
   [InlineData("different-callback")]
   [InlineData("fragment")]
   [InlineData("wrong-issuer")]
   [InlineData("expired")]
   public async Task InvalidCallbackConsumesAttemptWithoutExchange(string fault)
   {
      using Handler handler = OAuthHandler();
      var time = new FakeTime();
      using var client = new QqqSessionClient(Origin, handler, time);
      QqqBrowserLogin attempt = await client.BeginBrowserLoginAsync(Redirect);
      string state = Query(attempt.AuthorizationUri)["state"];
      Uri callback = fault switch
      {
         "wrong-state" => Callback(attempt, "wrong"),
         "duplicate-state" => new Uri(Callback(attempt, state).AbsoluteUri + "&state=" + state),
         "different-callback" => new Uri("io.qrun.qqq.print://elsewhere/oauth2redirect?code=one-time-code&state=" + state),
         "fragment" => new Uri(Callback(attempt, state).AbsoluteUri + "#fragment"),
         "wrong-issuer" => new Uri(Callback(attempt, state).AbsoluteUri + "&iss=https%3A%2F%2Fother.invalid"),
         _ => Callback(attempt, state),
      };
      if(string.Equals(fault, "expired", StringComparison.Ordinal))
      { time.Advance(TimeSpan.FromMinutes(6)); }
      await Assert.ThrowsAsync<QqqSessionException>(() => client.CompleteBrowserLoginAsync(attempt, callback));
      await Assert.ThrowsAsync<QqqSessionException>(() => client.CompleteBrowserLoginAsync(attempt, Callback(attempt, state)));
      Assert.Equal(3, handler.Requests.Count);
      Assert.False(client.IsAuthenticated);
   }



   /// <summary>If the provider's own discovery document names a different issuer than the server advertised, no browser URL is produced, which stops a swapped or spoofed identity provider.</summary>
   [Fact]
   public async Task DiscoveryIssuerMismatchCannotLaunchBrowser()
   {
      using var handler = new Handler();
      handler.Add(Discovery);
      handler.Add(OAuth);
      handler.Add(Provider.Replace("https://id.example.invalid/application/", "https://evil.invalid/", StringComparison.Ordinal));
      using var client = new QqqSessionClient(Origin, handler);
      await Assert.ThrowsAsync<QqqSessionException>(() => client.BeginBrowserLoginAsync(Redirect));
      Assert.All(handler.Requests, AssertPublic);
   }



   /// <summary>
   /// The identity key depends only on origin, principal, tenant and device, so it stays the same when the session token rotates,
   /// and fields containing separator characters cannot be shifted between positions to collide with another identity.
   /// </summary>
   [Fact]
   public async Task StableBindingExcludesRotatingCredentialsAndHasUnambiguousIdentity()
   {
      using Handler handler = TableHandler();
      handler.Add("""{"uuid":"session-one"}""");
      handler.Add(Identity);
      handler.Add("""{"uuid":"session-two"}""");
      handler.Add(Identity);
      using var client = new QqqSessionClient(Origin, handler);
      await client.SignInWithPasswordAsync("user", "password");
      QqqSessionBinding identity = await client.BindDeviceAsync(new QqqDeviceCredential("device-1", "proof"));
      string key = identity.IdentityKey;
      await client.ResumeSessionAsync();
      Assert.Equal("session-two", client.ExportCredential().SessionToken);
      Assert.Equal(key, new QqqSessionBinding(Origin, "user-1", "tenant-1", "device-1").IdentityKey);
      Assert.NotEqual(new QqqSessionBinding(Origin, "alpha\nbeta", "gamma", "device").IdentityKey,
         new QqqSessionBinding(Origin, "alpha", "beta\ngamma", "device").IdentityKey, StringComparer.Ordinal);
   }



   /// <summary>
   /// A slow document download must not hold up a control poll made on the same client, and signing out while it is in flight
   /// wins: when the download later completes, it fails and the client stays signed out instead of reviving the session.
   /// </summary>
   [Fact]
   public async Task LongPrintingResponseDoesNotBlockControlAndCannotRestoreLoggedOutSession()
   {
      using Handler handler = TableHandler();
      handler.Add("""{"uuid":"session"}""");
      handler.Add(Identity);
      var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
      var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      handler.AddAsync(_ => { entered.SetResult(); return pending.Task; });
      handler.Add("{}");
      handler.Add("{}");
      using var client = new QqqSessionClient(Origin, handler);
      await client.SignInWithPasswordAsync("user", "password");
      await client.BindDeviceAsync(new QqqDeviceCredential("device-1", "proof"));
      Task<HttpResponseMessage> printing = client.SendPrintingAsync(HttpMethod.Get, "device/jobs/job-1/content");
      await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
      using HttpResponseMessage control = await client.SendPrintingAsync(HttpMethod.Post, "device/control/poll").WaitAsync(TimeSpan.FromSeconds(5));
      await client.SignOutAsync().WaitAsync(TimeSpan.FromSeconds(5));
      pending.SetResult(Response("{}"));
      await Assert.ThrowsAsync<QqqSessionException>(async () => { using HttpResponseMessage response = await printing; });
      Assert.False(client.IsAuthenticated);
   }



   /// <summary>A pre-login discovery response over 64 KiB is rejected after one public request, bounding what an untrusted server can make the client read.</summary>
   [Fact]
   public async Task OversizedPublicResponseIsRejectedBeforeCredentials()
   {
      using var handler = new Handler();
      handler.Add(new string('x', 65_537));
      using var client = new QqqSessionClient(Origin, handler);
      await Assert.ThrowsAsync<QqqSessionException>(() => client.SignInWithPasswordAsync("user", "password"));
      AssertPublic(Assert.Single(handler.Requests));
   }



   /// <summary>Builds the provider's redirect back to the app: the attempt's own redirect address plus a one-time code and the given state.</summary>
   private static Uri Callback(QqqBrowserLogin attempt, string state) => new(attempt.RedirectUri.AbsoluteUri + "?code=one-time-code&state=" + Uri.EscapeDataString(state));



   /// <summary>Splits a URL's query string into decoded name/value pairs so tests can assert individual authorization parameters.</summary>
   private static Dictionary<string, string> Query(Uri uri) => uri.Query[1..].Split('&').Select(part => part.Split('=', 2))
      .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]), StringComparer.Ordinal);



   /// <summary>A fake server pre-loaded with discovery and a table-based (password) authentication description, the first two public replies of a password sign-in.</summary>
   private static Handler TableHandler()
   {
      var handler = new Handler();
      handler.Add(Discovery);
      handler.Add(Table);
      return handler;
   }



   /// <summary>A fake server pre-loaded with discovery, the native browser-login description and the provider's discovery document, the three public replies before a browser sign-in starts.</summary>
   private static Handler OAuthHandler()
   {
      var handler = new Handler();
      handler.Add(Discovery);
      handler.Add(OAuth);
      handler.Add(Provider);
      return handler;
   }



   /// <summary>Asserts a request was anonymous: no Authorization header, cookie, device id or device token, as every request made before credentials are allowed must be.</summary>
   private static void AssertPublic(Request request)
   {
      Assert.Null(request.Authorization);
      Assert.Null(request.Cookie);
      Assert.Null(request.DeviceId);
      Assert.Null(request.DeviceToken);
   }



   /// <summary>A JSON response with the given status, optionally carrying a Set-Cookie header.</summary>
   private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK, string? cookie = null)
   {
      var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
      if(cookie is not null)
      { response.Headers.Add("Set-Cookie", cookie); }
      return response;
   }



   /// <summary>What the fake server saw for one request: URL, body and the credential-bearing headers, so tests can prove where each secret was and was not sent.</summary>
   private sealed record Request(Uri Uri, string? Body, string? Authorization, string? Cookie, string? DeviceId, string? DeviceToken, string? ClientChannel, string? Origin);



   /// <summary>Scripted fake server: it records every request and answers them in the order replies were queued, so a test fixes the exact conversation and fails if the client deviates.</summary>
   private sealed class Handler : HttpMessageHandler
   {
      private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _responses = new();
      internal List<Request> Requests { get; } = [];



      /// <summary>Queues a JSON reply with the given status and optional cookie.</summary>
      internal void Add(string body, HttpStatusCode status = HttpStatusCode.OK, string? cookie = null) => AddResponse(() => Response(body, status, cookie));



      /// <summary>Queues a reply built on demand, for responses that need custom headers.</summary>
      internal void AddResponse(Func<HttpResponseMessage> response) => AddAsync(_ => Task.FromResult(response()));



      /// <summary>Queues an asynchronous reply, for responses a test wants to hold open and release later.</summary>
      internal void AddAsync(Func<CancellationToken, Task<HttpResponseMessage>> response) => _responses.Enqueue(response);



      /// <summary>Records the request, including body and credential headers, then returns the next queued reply.</summary>
      protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
         string? Header(string name) => request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join("; ", values) : null;
         Requests.Add(new Request(request.RequestUri!, body, request.Headers.Authorization?.ToString(), Header("Cookie"),
            Header("X-QQQ-Device-Id"), Header("X-QQQ-Device-Token"), Header("X-QQQ-Client"), Header("Origin")));
         return await _responses.Dequeue()(cancellationToken);
      }
   }



   /// <summary>A clock that moves only when told to, so the five-minute sign-in expiry can be tested without waiting.</summary>
   private sealed class FakeTime : TimeProvider
   {
      private long _timestamp;



      /// <summary>One tick per timestamp unit, so timestamps and TimeSpan ticks are the same scale.</summary>
      public override long TimestampFrequency => TimeSpan.TicksPerSecond;



      /// <summary>The current fake timestamp.</summary>
      public override long GetTimestamp() => _timestamp;



      /// <summary>Moves the fake clock forward.</summary>
      internal void Advance(TimeSpan amount) => _timestamp += amount.Ticks;
   }
}
