/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Transport;

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>Origin-bound QQQ session transport. Provider token refresh remains entirely on the QQQ server.</summary>
public sealed partial class QqqSessionClient : IQqqSessionClient
{
   public const string ClientChannel = "api/qqq-print-agent";
   public const string PrintingPath = "/qqq/v1/qbit/printing/";
   private const int MetadataLimit = 65_536;
   private const int PrintingLimit = 22 * 1024 * 1024;
   private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };
   private readonly HttpClient _http;
   private readonly TimeProvider _timeProvider;
   private readonly ApplicationTrafficMeter? _traffic;
   private readonly SemaphoreSlim _gate = new(1, 1);
   private CookieContainer _cookies = new();
   private QqqApiDiscovery? _discovery;
   private QqqAuthenticationMetadata? _authentication;
   private string? _sessionToken;
   private QqqDeviceCredential? _device;
   private QqqSessionBinding? _binding;
   private QqqBrowserLogin? _browserLogin;
   private long _generation;



   /// <summary>
   /// Creates a client bound to one server origin. The origin must be an absolute https URL with no credentials, path,
   /// query or fragment (plain http only when explicitly allowed and loopback). The handler never follows redirects or
   /// manages cookies, because redirects are refused and the session cookie is attached by hand to each request, and the
   /// HTTP client has no overall timeout because every request sets its own bound.
   /// </summary>
   public QqqSessionClient(Uri origin, HttpMessageHandler? handler = null, TimeProvider? timeProvider = null, bool allowLoopbackHttp = false, ApplicationTrafficMeter? traffic = null)
   {
      ArgumentNullException.ThrowIfNull(origin);
      if(!origin.IsAbsoluteUri || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0
         || !string.Equals(origin.AbsolutePath, "/", StringComparison.Ordinal)
         || (!string.Equals(origin.Scheme, "https", StringComparison.Ordinal)
            && !(allowLoopbackHttp && string.Equals(origin.Scheme, "http", StringComparison.Ordinal) && origin.IsLoopback)))
      {
         throw new ArgumentException("Use an HTTPS server origin, or explicitly enabled loopback HTTP.", nameof(origin));
      }
      Origin = new Uri(origin.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
      _timeProvider = timeProvider ?? TimeProvider.System;
      _traffic = traffic;
      _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
      {
         Timeout = Timeout.InfiniteTimeSpan,
      };
   }



   public Uri Origin { get; }
   public bool IsAuthenticated => _sessionToken is not null;



   /// <summary>Returns the validated discovery document, fetching it when none is cached; the client gate makes concurrent callers share one fetch.</summary>
   public async Task<QqqApiDiscovery> DiscoverAsync(CancellationToken cancellationToken = default)
   {
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      { return await DiscoverCoreAsync(cancellationToken).ConfigureAwait(false); }
      finally { _gate.Release(); }
   }



   /// <summary>Returns the server's authentication type (table-based, or OAuth2 with a checked native grant), fetching and validating it on first use.</summary>
   public async Task<QqqAuthenticationMetadata> GetAuthenticationMetadataAsync(CancellationToken cancellationToken = default)
   {
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      { return await AuthenticationCoreAsync(cancellationToken).ConfigureAwait(false); }
      finally { _gate.Release(); }
   }



   /// <summary>
   /// Signs in with a table-based user name and password. Input is validated first (bounded length, no control characters,
   /// no colon in the name) because the pair travels as HTTP Basic. Only a TABLE_BASED server accepts it, any previous
   /// session is cleared, and the password is used for this one request and never stored.
   /// </summary>
   public async Task SignInWithPasswordAsync(string username, string password, CancellationToken cancellationToken = default)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(username);
      ArgumentException.ThrowIfNullOrWhiteSpace(password);
      if(username.Length > 1024 || username.Contains(':', StringComparison.Ordinal) || username.Any(char.IsControl))
      {
         throw new ArgumentException("Invalid sign-in input.", nameof(username));
      }
      if(password.Length > 4096 || password.Any(character => character is '\r' or '\n'))
      {
         throw new ArgumentException("Invalid sign-in input.", nameof(password));
      }
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
         QqqAuthenticationMetadata authentication = await AuthenticationCoreAsync(cancellationToken).ConfigureAwait(false);
         if(!string.Equals(authentication.Type, "TABLE_BASED", StringComparison.Ordinal))
         { throw QqqApiDiscovery.Configuration(); }
         ClearSession();
         string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
         using HttpResponseMessage response = await SendCoreAsync(new Uri(Origin, "/qqq/v1/manageSession"), HttpMethod.Post, JsonBody(new { }),
            MetadataLimit, null, null, basic, cancellationToken).ConfigureAwait(false);
         RequireSuccess(response);
         AdoptManagedSession(response);
      }
      finally { _gate.Release(); }
   }



   /// <summary>
   /// Re-adopts a saved session after a restart. The credential must belong to this origin and the server must confirm it
   /// with a resume request; any failure clears the session so a stale token is never kept.
   /// </summary>
   public async Task RestoreSessionAsync(QqqSessionCredential credential, CancellationToken cancellationToken = default)
   {
      ArgumentNullException.ThrowIfNull(credential);
      if(credential.Origin != Origin)
      { throw new ArgumentException("The session belongs to another server.", nameof(credential)); }
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
         await AuthenticationCoreAsync(cancellationToken).ConfigureAwait(false);
         ClearSession();
         SetSession(credential.SessionToken);
         await ResumeCoreAsync(cancellationToken).ConfigureAwait(false);
      }
      catch
      {
         ClearSession();
         throw;
      }
      finally { _gate.Release(); }
   }



   /// <summary>Renews the current session with the server and, when an identity is bound, re-checks that it has not changed.</summary>
   public async Task ResumeSessionAsync(CancellationToken cancellationToken = default)
   {
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      { await ResumeCoreAsync(cancellationToken).ConfigureAwait(false); }
      finally { _gate.Release(); }
   }



   /// <summary>Ends the session on the server when one exists and always clears it locally, even if the server call fails.</summary>
   public async Task SignOutAsync(CancellationToken cancellationToken = default)
   {
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
         if(_sessionToken is not null)
         {
            using HttpResponseMessage response = await SendCoreAsync(new Uri(Origin, "/qqq/v1/logout"), HttpMethod.Post, JsonBody(new { }),
               MetadataLimit, CookieHeader(), null, null, cancellationToken).ConfigureAwait(false);
            RequireSuccess(response);
         }
      }
      finally
      {
         ClearSession();
         _gate.Release();
      }
   }



   /// <summary>Returns the credential a later start can restore this session from; throws when not signed in.</summary>
   public QqqSessionCredential ExportCredential() => new(Origin, _sessionToken ?? throw AuthenticationRequired());



   /// <summary>Asks the server who the session belongs to: user, tenant and, when a device is bound, that device.</summary>
   public async Task<QqqSessionBinding> GetSessionIdentityAsync(CancellationToken cancellationToken = default)
   {
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      { return await IdentityCoreAsync(_device, cancellationToken).ConfigureAwait(false); }
      finally { _gate.Release(); }
   }



   /// <summary>
   /// Binds the paired device to the signed-in session. A legacy device is first claimed on the server; the identity the
   /// server then reports must name the same device or the bind is rejected. A successful bind advances the generation, so
   /// requests started under the previous binding are refused instead of completing against the new one.
   /// </summary>
   public async Task<QqqSessionBinding> BindDeviceAsync(QqqDeviceCredential credential, bool claimLegacyDevice = false, CancellationToken cancellationToken = default)
   {
      ArgumentNullException.ThrowIfNull(credential);
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
         RequireAuthenticated();
         if(claimLegacyDevice)
         {
            using HttpResponseMessage response = await SendCoreAsync(new Uri(Origin, PrintingPath + "device/bind"), HttpMethod.Post, JsonBody(new { }),
               MetadataLimit, CookieHeader(), credential, null, cancellationToken).ConfigureAwait(false);
            RequireSuccess(response);
         }
         QqqSessionBinding identity = await IdentityCoreAsync(credential, cancellationToken).ConfigureAwait(false);
         if(!string.Equals(identity.DeviceId, credential.DeviceId, StringComparison.Ordinal))
         { throw InvalidResponse(); }
         _device = credential;
         _binding = identity;
         _generation++;
         return identity;
      }
      finally { _gate.Release(); }
   }



   /// <summary>Only native printing routes are accepted. Network I/O does not hold the state lock or delay control traffic behind printing.</summary>
   public async Task<HttpResponseMessage> SendPrintingAsync(HttpMethod method, string relativePath, HttpContent? content = null, CancellationToken cancellationToken = default)
   {
      ArgumentNullException.ThrowIfNull(method);
      Uri uri = PrintingUri(relativePath);
      string cookie;
      QqqDeviceCredential? device;
      long generation;
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
         await DiscoverCoreAsync(cancellationToken).ConfigureAwait(false);
         RequireAuthenticated();
         device = relativePath.StartsWith("device/", StringComparison.Ordinal) ? _device : null;
         if(relativePath.StartsWith("device/", StringComparison.Ordinal) && (device is null || _binding?.HasApprovedDevice != true))
         {
            throw new QqqSessionException(QqqSessionException.FailureKind.PermissionDenied, "An approved device binding is required.");
         }
         if(!relativePath.StartsWith("device/", StringComparison.Ordinal) && !relativePath.StartsWith("admin/", StringComparison.Ordinal)
            && !string.Equals(relativePath, "devices/enroll", StringComparison.Ordinal))
         {
            throw new ArgumentException("Only native printing operations are accepted.", nameof(relativePath));
         }
         cookie = CookieHeader();
         generation = _generation;
      }
      finally { _gate.Release(); }
      bool document = method == HttpMethod.Get && relativePath.StartsWith("device/jobs/", StringComparison.Ordinal) && relativePath.EndsWith("/content", StringComparison.Ordinal);
      HttpResponseMessage response = await SendCoreAsync(uri, method, content, PrintingLimit, cookie, device, null, cancellationToken, streamContent: document, boundedByCaller: true).ConfigureAwait(false);
      bool acquired = false;
      try
      {
         await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
         acquired = true;
         if(generation != _generation)
         { throw AuthenticationRequired(); }
         CheckAuthorization(response);
         string? returned = ReadSessionCookie(response);
         if(returned is not null && !string.Equals(returned, _sessionToken, StringComparison.Ordinal))
         { throw InvalidResponse(); }
         return response;
      }
      catch
      {
         response.Dispose();
         throw;
      }
      finally { if(acquired) { _gate.Release(); } }
   }



   /// <summary>Fetches and validates the discovery document and caches it; callers hold the client gate.</summary>
   private async Task<QqqApiDiscovery> DiscoverCoreAsync(CancellationToken cancellationToken)
   {
      if(_discovery is not null)
      { return _discovery; }
      using HttpResponseMessage response = await SendCoreAsync(new Uri(Origin, "/qqq/v1/version"), HttpMethod.Get, null, MetadataLimit,
         null, null, null, cancellationToken).ConfigureAwait(false);
      RequireSuccess(response);
      QqqApiDiscovery discovery = Parse<QqqApiDiscovery>(response);
      discovery.Validate();
      _discovery = discovery;
      return discovery;
   }



   /// <summary>
   /// Fetches the authentication metadata after discovery and caches it. Only table-based and OAuth2 are accepted, and an
   /// OAuth2 server must offer the native-client grant this agent can use, otherwise the server is reported as unsupported.
   /// </summary>
   private async Task<QqqAuthenticationMetadata> AuthenticationCoreAsync(CancellationToken cancellationToken)
   {
      await DiscoverCoreAsync(cancellationToken).ConfigureAwait(false);
      if(_authentication is not null)
      { return _authentication; }
      using HttpResponseMessage response = await SendCoreAsync(new Uri(Origin, "/qqq/v1/metaData/authentication"), HttpMethod.Get, null, MetadataLimit,
         null, null, null, cancellationToken).ConfigureAwait(false);
      RequireSuccess(response);
      QqqAuthenticationMetadata metadata = Parse<QqqAuthenticationMetadata>(response);
      if(!string.Equals(metadata.Type, "TABLE_BASED", StringComparison.Ordinal) && !string.Equals(metadata.Type, "OAUTH2", StringComparison.Ordinal))
      {
         throw QqqApiDiscovery.Configuration();
      }
      if(string.Equals(metadata.Type, "OAUTH2", StringComparison.Ordinal))
      { ValidateNativeGrant(metadata); }
      _authentication = metadata;
      return metadata;
   }



   /// <summary>Asks the server to renew the managed session, adopts the returned token, and re-verifies a bound identity.</summary>
   private async Task ResumeCoreAsync(CancellationToken cancellationToken)
   {
      RequireAuthenticated();
      using HttpResponseMessage response = await SendCoreAsync(new Uri(Origin, "/qqq/v1/manageSession"), HttpMethod.Post, JsonBody(new { }),
         MetadataLimit, CookieHeader(), null, null, cancellationToken).ConfigureAwait(false);
      RequireSuccess(response);
      AdoptManagedSession(response);
      if(_binding is not null)
      { await IdentityCoreAsync(_device, cancellationToken).ConfigureAwait(false); }
   }



   /// <summary>
   /// Reads the printing session identity and refuses any change. Once a principal and tenant are bound, a response naming
   /// a different one, or a different device than the credential's, clears the session and reports permission denied, so
   /// the agent can never silently continue as another user.
   /// </summary>
   private async Task<QqqSessionBinding> IdentityCoreAsync(QqqDeviceCredential? credential, CancellationToken cancellationToken)
   {
      RequireAuthenticated();
      using HttpResponseMessage response = await SendCoreAsync(new Uri(Origin, PrintingPath + "session"), HttpMethod.Get, null,
         MetadataLimit, CookieHeader(), credential, null, cancellationToken).ConfigureAwait(false);
      RequireSuccess(response);
      using JsonDocument document = ParseDocument(response);
      JsonElement root = document.RootElement;
      string principal = IdentityValue(root, "principalId");
      string tenant = IdentityValue(root, "tenantId");
      string? device = root.TryGetProperty("deviceId", out JsonElement value) && value.ValueKind != JsonValueKind.Null ? IdentityValue(root, "deviceId") : null;
      var identity = new QqqSessionBinding(Origin, principal, tenant, device);
      if((_binding is not null && (!string.Equals(_binding.PrincipalId, principal, StringComparison.Ordinal)
            || !string.Equals(_binding.TenantId, tenant, StringComparison.Ordinal)))
         || (credential is not null && !string.Equals(credential.DeviceId, device, StringComparison.Ordinal)))
      {
         ClearSession();
         throw new QqqSessionException(QqqSessionException.FailureKind.PermissionDenied, "The server returned a different principal or device binding.");
      }
      _binding ??= identity;
      return identity;
   }



   /// <summary>
   /// The single place a request leaves this client. It refuses any URL outside the server origin (except provider
   /// discovery), marks the request as this agent's channel, attaches the session cookie, device headers or Basic
   /// credential that the caller passed, refuses redirects, and either buffers the body within the size bound or, for a
   /// successful document download, hands back the live stream. The default 20 second limit is skipped only when the caller
   /// bounds the request itself.
   /// </summary>
   private async Task<HttpResponseMessage> SendCoreAsync(Uri uri, HttpMethod method, HttpContent? content, int maximumBytes, string? cookie,
      QqqDeviceCredential? device, string? basic, CancellationToken cancellationToken, bool providerDiscovery = false, bool streamContent = false, bool boundedByCaller = false)
   {
      if(!providerDiscovery && !SameOrigin(uri, Origin))
      { throw new ArgumentException("Cross-origin QQQ requests are forbidden.", nameof(uri)); }
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      if(!boundedByCaller)
      { timeout.CancelAfter(TimeSpan.FromSeconds(20)); }
      using var request = new HttpRequestMessage(method, uri)
      {
         Content = !providerDiscovery && _traffic is not null && content is not null ? new TrafficHttpContent(content, _traffic.RecordUpload) : content,
      };
      request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
      if(!providerDiscovery)
      { request.Headers.Add("X-QQQ-Client", ClientChannel); }
      if(cookie is not null)
      {
         request.Headers.Add("Cookie", cookie);
         if(method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options)
         {
            request.Headers.Add("Origin", Origin.GetLeftPart(UriPartial.Authority));
         }
      }
      if(basic is not null)
      { request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic); }
      if(device is not null)
      {
         request.Headers.Add("X-QQQ-Device-Id", device.DeviceId);
         request.Headers.Add("X-QQQ-Device-Token", device.DeviceToken);
      }
      HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
      try
      {
         if(!providerDiscovery && _traffic is not null)
         { response.Content = new TrafficHttpContent(response.Content, _traffic.RecordDownload); }
         if((int)response.StatusCode is >= 300 and < 400)
         {
            throw new QqqSessionException(QqqSessionException.FailureKind.TransportRefused, "Redirected authentication or API requests are refused.");
         }
         if(streamContent && response.IsSuccessStatusCode)
         {
            response.RequestMessage = null;
            return response;
         }
         await BufferResponseAsync(response, maximumBytes, timeout.Token).ConfigureAwait(false);
         response.RequestMessage = null;
         return response;
      }
      catch
      {
         response.Dispose();
         throw;
      }
   }



   /// <summary>
   /// Reads a response body into memory but never more than the stated limit, whether announced by Content-Length or
   /// discovered while reading, so a hostile or broken server cannot exhaust memory. The buffered content keeps the original headers.
   /// </summary>
   private static async Task BufferResponseAsync(HttpResponseMessage response, int maximumBytes, CancellationToken token)
   {
      if(response.Content.Headers.ContentLength > maximumBytes)
      { throw InvalidResponse(); }
      await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
      using var output = new MemoryStream();
      byte[] buffer = new byte[8192];
      int count;
      while((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
      {
         if(output.Length + count > maximumBytes)
         { throw InvalidResponse(); }
         output.Write(buffer, 0, count);
      }
      var buffered = new ByteArrayContent(output.ToArray());
      foreach(KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
      { buffered.Headers.TryAddWithoutValidation(header.Key, header.Value); }
      response.Content.Dispose();
      response.Content = buffered;
   }



   /// <summary>
   /// Takes the session token from a manage-session response, from its body, its cookie or both. When both are present they
   /// must agree and the token must be well formed, otherwise the session is cleared and the response rejected.
   /// </summary>
   private void AdoptManagedSession(HttpResponseMessage response)
   {
      using JsonDocument document = ParseDocument(response);
      string? body = document.RootElement.TryGetProperty("uuid", out JsonElement value) && value.ValueKind != JsonValueKind.Null
         ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw InvalidResponse() : null;
      string? cookie = ReadSessionCookie(response);
      if(body is not null && cookie is not null && !string.Equals(body, cookie, StringComparison.Ordinal))
      { ClearSession(); throw InvalidResponse(); }
      string token = cookie ?? body ?? _sessionToken ?? throw InvalidResponse();
      try
      { SetSession(new QqqSessionCredential(Origin, token).SessionToken); }
      catch(ArgumentException) { ClearSession(); throw InvalidResponse(); }
   }



   /// <summary>
   /// Extracts the session token from Set-Cookie headers, strictly. Headers are size-bounded, the cookie must be for this
   /// host with path "/", and repeated session cookies must agree. An expired, empty or deleted session cookie means the
   /// server ended the session, so the local session is cleared and sign-in is required.
   /// </summary>
   private string? ReadSessionCookie(HttpResponseMessage response)
   {
      if(!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? headers))
      { return null; }
      string? token = null;
      foreach(string header in headers)
      {
         if(header.Length > 8192)
         { throw InvalidResponse(); }
         var parsed = new CookieContainer();
         try
         { parsed.SetCookies(Origin, header); }
         catch(CookieException) { throw InvalidResponse(); }
         string name = header.Split('=', 2)[0].Trim();
         if((name is "sessionUUID" or "sessionId") && parsed.GetAllCookies().Count == 0)
         {
            ClearSession();
            throw AuthenticationRequired();
         }
         foreach(Cookie cookie in parsed.GetAllCookies())
         {
            if(cookie.Name is not "sessionUUID" and not "sessionId")
            { continue; }
            if(!string.Equals(cookie.Domain.TrimStart('.'), Origin.Host, StringComparison.OrdinalIgnoreCase)
               || !string.Equals(cookie.Path, "/", StringComparison.Ordinal) || (token is not null && !string.Equals(token, cookie.Value, StringComparison.Ordinal)))
            {
               throw InvalidResponse();
            }
            if(cookie.Expired || string.IsNullOrEmpty(cookie.Value))
            { ClearSession(); throw AuthenticationRequired(); }
            token = cookie.Value;
         }
      }
      return token;
   }



   /// <summary>
   /// Makes a token the current session and rebuilds the cookie jar under both cookie names QQQ has used. The generation
   /// advances only when the token changed, so a renewal to the same token does not invalidate in-flight requests.
   /// </summary>
   private void SetSession(string token)
   {
      if(!string.Equals(_sessionToken, token, StringComparison.Ordinal))
      { _generation++; }
      _sessionToken = token;
      _cookies = new CookieContainer();
      _cookies.Add(Origin, new Cookie("sessionUUID", token, "/") { HttpOnly = true, Secure = string.Equals(Origin.Scheme, "https", StringComparison.Ordinal) });
      _cookies.Add(Origin, new Cookie("sessionId", token, "/") { HttpOnly = true, Secure = string.Equals(Origin.Scheme, "https", StringComparison.Ordinal) });
   }



   /// <summary>Forgets the token, cookies, device and bound identity and advances the generation, so every request already in flight is refused when it returns.</summary>
   private void ClearSession()
   {
      _sessionToken = null;
      _cookies = new CookieContainer();
      _device = null;
      _binding = null;
      _browserLogin = null;
      _generation++;
   }



   /// <summary>The Cookie header value for the current session, scoped to this origin.</summary>
   private string CookieHeader() => _cookies.GetCookieHeader(Origin);



   /// <summary>Throws the sign-in-required failure when no session exists.</summary>
   private void RequireAuthenticated() { if(_sessionToken is null) { throw AuthenticationRequired(); } }



   /// <summary>The failure meaning the user must sign in again; the message is safe to show and carries no server detail.</summary>
   private static QqqSessionException AuthenticationRequired() => new(QqqSessionException.FailureKind.AuthenticationRequired, "Sign in to QQQ to continue.");



   /// <summary>The failure for any response that breaks the expected shape; deliberately generic so a hostile body is never echoed.</summary>
   private static QqqSessionException InvalidResponse() => new(QqqSessionException.FailureKind.InvalidResponse, "The server returned an invalid authentication or API response.");



   /// <summary>
   /// Maps 401 to a cleared session and sign-in required, and 403 to permission denied. They are kept apart because a
   /// withdrawn session can be repaired by signing in, whereas a denied permission cannot.
   /// </summary>
   private void CheckAuthorization(HttpResponseMessage response)
   {
      if(response.StatusCode == HttpStatusCode.Unauthorized)
      { ClearSession(); throw AuthenticationRequired(); }
      if(response.StatusCode == HttpStatusCode.Forbidden)
      {
         throw new QqqSessionException(QqqSessionException.FailureKind.PermissionDenied, "The current QQQ user is not permitted to perform this operation.");
      }
   }



   /// <summary>Applies the authorization mapping first, then treats any other non-success status as a refused request with a generic message.</summary>
   private void RequireSuccess(HttpResponseMessage response)
   {
      CheckAuthorization(response);
      if(!response.IsSuccessStatusCode)
      {
         throw new QqqSessionException(QqqSessionException.FailureKind.TransportRefused, "The QQQ request did not succeed.");
      }
   }



   /// <summary>
   /// Builds a URL under the printing route from a caller-supplied relative path, rejecting absolute, backslash, fragment,
   /// percent-encoded, dot-segment and empty-segment paths so a caller cannot reach another route or origin by crafting the path.
   /// </summary>
   private Uri PrintingUri(string path)
   {
      if(string.IsNullOrEmpty(path) || path.StartsWith('/') || path.Contains('\\', StringComparison.Ordinal)
         || path.Contains('#', StringComparison.Ordinal) || path.Split('?', 2)[0].Contains('%', StringComparison.Ordinal)
         || path.Split('?', 2)[0].Split('/').Any(part => part is "." or ".." or ""))
      {
         throw new ArgumentException("Use a relative native printing route.", nameof(path));
      }
      var uri = new Uri(Origin, PrintingPath + path);
      if(!SameOrigin(uri, Origin) || !uri.AbsolutePath.StartsWith(PrintingPath, StringComparison.Ordinal))
      { throw new ArgumentException("Invalid printing route.", nameof(path)); }
      return uri;
   }



   /// <summary>Compares scheme, host and port only, ignoring case; used to refuse cross-origin requests.</summary>
   private static bool SameOrigin(Uri left, Uri right) => left.IsAbsoluteUri && right.IsAbsoluteUri
      && string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
      && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;



   /// <summary>Serializes a request body as JSON with the client's strict, case-sensitive options.</summary>
   private static StringContent JsonBody<T>(T body) => new(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");



   /// <summary>Deserializes a response into a type, reporting a missing or malformed body as the generic invalid-response failure.</summary>
   private static T Parse<T>(HttpResponseMessage response)
   {
      using JsonDocument document = ParseDocument(response);
      try
      { return document.RootElement.Deserialize<T>(JsonOptions) ?? throw InvalidResponse(); }
      catch(JsonException) { throw InvalidResponse(); }
   }



   /// <summary>
   /// Parses a response as a JSON object with a depth limit and no duplicate property names (a common smuggling trick for
   /// disagreeing parsers). Anything else is an invalid response; the caller owns the returned document.
   /// </summary>
   private static JsonDocument ParseDocument(HttpResponseMessage response)
   {
      try
      {
         using Stream stream = response.Content.ReadAsStream();
         var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
         try
         {
            if(document.RootElement.ValueKind != JsonValueKind.Object)
            { throw InvalidResponse(); }
            RejectDuplicateProperties(document.RootElement);
            return document;
         }
         catch { document.Dispose(); throw; }
      }
      catch(JsonException) { throw InvalidResponse(); }
   }



   /// <summary>Walks objects and arrays and rejects any object that repeats a property name, ignoring case.</summary>
   private static void RejectDuplicateProperties(JsonElement element)
   {
      if(element.ValueKind == JsonValueKind.Object)
      {
         var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
         foreach(JsonProperty property in element.EnumerateObject())
         {
            if(!names.Add(property.Name))
            { throw InvalidResponse(); }
            RejectDuplicateProperties(property.Value);
         }
      }
      else if(element.ValueKind == JsonValueKind.Array)
      {
         foreach(JsonElement child in element.EnumerateArray())
         { RejectDuplicateProperties(child); }
      }
   }



   /// <summary>Reads a required identity string that must be non-blank, at most 1024 characters and free of control characters.</summary>
   private static string IdentityValue(JsonElement root, string name)
   {
      if(!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String
         || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 1024 || value.GetString()!.Any(char.IsControl))
      {
         throw InvalidResponse();
      }
      return value.GetString()!;
   }



   /// <summary>Clears the session, so a late response is refused, and releases the HTTP client and gate.</summary>
   public void Dispose()
   {
      ClearSession();
      _http.Dispose();
      _gate.Dispose();
   }
}
