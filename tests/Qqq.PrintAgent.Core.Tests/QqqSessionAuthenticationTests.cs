using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Core.Tests;

public sealed class QqqSessionAuthenticationTests
{
   private static readonly Uri Origin = new("https://printing.example.invalid");
   private const string Discovery = """{"apiVersion":"1.9.0","features":["version","metaData","processes","processCancel","extensions","widgets","nativeSessionLogin"],"clientChannels":["api"],"extensions":[{"name":"printing","version":"1.0.0","pathPrefix":"/qbit/printing"}]}""";
   private const string Table = """{"name":"local","type":"TABLE_BASED"}""";
   private const string Identity = """{"principalId":"user-1","tenantId":"tenant-1","deviceId":"device-1"}""";
   private const string OAuth = """{"name":"configured-provider","type":"OAUTH2","values":{"clientId":"qqq-client","scopes":"openid profile offline_access","nativeClientId":"not-the-session-client","nativeSessionLogin":{"flow":"AUTHORIZATION_CODE_PKCE_SESSION","codeChallengeMethod":"S256","prompt":"login","issuer":"https://id.example.invalid/application/","discoveryUrl":"https://id.example.invalid/application/.well-known/openid-configuration","redirectUris":["io.qrun.qqq-print-agent:/oauth/callback"]}}}""";
   private const string Provider = """{"issuer":"https://id.example.invalid/application/","authorization_endpoint":"https://id.example.invalid/authorize","code_challenge_methods_supported":["S256"]}""";
   private static readonly Uri Redirect = new("io.qrun.qqq-print-agent:/oauth/callback");

   [Theory]
   [InlineData("1.9.0", "1.7.0")]
   [InlineData("1.9.0", "2.0.0")]
   [InlineData("1.9.0", "1.09.0")]
   [InlineData("\"api\"", "\"windows\"")]
   [InlineData("\"printing\"", "\"other\"")]
   [InlineData("\"1.0.0\"", "\"2.0.0\"")]
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

   [Fact]
   public async Task AnotherOriginCredentialIsRejectedWithoutNetwork()
   {
      using var handler = new Handler();
      using var client = new QqqSessionClient(Origin, handler);
      await Assert.ThrowsAsync<ArgumentException>(() => client.RestoreSessionAsync(new QqqSessionCredential(new Uri("https://another.invalid"), "secret")));
      Assert.Empty(handler.Requests);
   }

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

   [Fact]
   public async Task OversizedPublicResponseIsRejectedBeforeCredentials()
   {
      using var handler = new Handler();
      handler.Add(new string('x', 65_537));
      using var client = new QqqSessionClient(Origin, handler);
      await Assert.ThrowsAsync<QqqSessionException>(() => client.SignInWithPasswordAsync("user", "password"));
      AssertPublic(Assert.Single(handler.Requests));
   }

   private static Uri Callback(QqqBrowserLogin attempt, string state) => new(attempt.RedirectUri.AbsoluteUri + "?code=one-time-code&state=" + Uri.EscapeDataString(state));
   private static Dictionary<string, string> Query(Uri uri) => uri.Query[1..].Split('&').Select(part => part.Split('=', 2))
      .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]), StringComparer.Ordinal);

   private static Handler TableHandler()
   {
      var handler = new Handler();
      handler.Add(Discovery);
      handler.Add(Table);
      return handler;
   }

   private static Handler OAuthHandler()
   {
      var handler = new Handler();
      handler.Add(Discovery);
      handler.Add(OAuth);
      handler.Add(Provider);
      return handler;
   }

   private static void AssertPublic(Request request)
   {
      Assert.Null(request.Authorization);
      Assert.Null(request.Cookie);
      Assert.Null(request.DeviceId);
      Assert.Null(request.DeviceToken);
   }

   private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK, string? cookie = null)
   {
      var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
      if(cookie is not null)
      { response.Headers.Add("Set-Cookie", cookie); }
      return response;
   }

   private sealed record Request(Uri Uri, string? Body, string? Authorization, string? Cookie, string? DeviceId, string? DeviceToken, string? ClientChannel, string? Origin);

   private sealed class Handler : HttpMessageHandler
   {
      private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _responses = new();
      internal List<Request> Requests { get; } = [];
      internal void Add(string body, HttpStatusCode status = HttpStatusCode.OK, string? cookie = null) => AddResponse(() => Response(body, status, cookie));
      internal void AddResponse(Func<HttpResponseMessage> response) => AddAsync(_ => Task.FromResult(response()));
      internal void AddAsync(Func<CancellationToken, Task<HttpResponseMessage>> response) => _responses.Enqueue(response);
      protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
         string? Header(string name) => request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join("; ", values) : null;
         Requests.Add(new Request(request.RequestUri!, body, request.Headers.Authorization?.ToString(), Header("Cookie"),
            Header("X-QQQ-Device-Id"), Header("X-QQQ-Device-Token"), Header("X-QQQ-Client"), Header("Origin")));
         return await _responses.Dequeue()(cancellationToken);
      }
   }

   private sealed class FakeTime : TimeProvider
   {
      private long _timestamp;
      public override long TimestampFrequency => TimeSpan.TicksPerSecond;
      public override long GetTimestamp() => _timestamp;
      internal void Advance(TimeSpan amount) => _timestamp += amount.Ticks;
   }
}
