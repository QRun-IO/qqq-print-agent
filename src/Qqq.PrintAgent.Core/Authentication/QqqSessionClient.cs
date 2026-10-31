/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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
   private readonly SemaphoreSlim _gate = new(1, 1);
   private CookieContainer _cookies = new();
   private QqqApiDiscovery? _discovery;
   private QqqAuthenticationMetadata? _authentication;
   private string? _sessionToken;
   private QqqDeviceCredential? _device;
   private QqqSessionBinding? _binding;
   private QqqBrowserLogin? _browserLogin;
   private long _generation;

   public QqqSessionClient(Uri origin, HttpMessageHandler? handler = null, TimeProvider? timeProvider = null, bool allowLoopbackHttp = false)
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
      _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
      {
         Timeout = Timeout.InfiniteTimeSpan,
      };
   }

   public Uri Origin { get; }
   public bool IsAuthenticated => _sessionToken is not null;

   public async Task<QqqApiDiscovery> DiscoverAsync(CancellationToken cancellationToken = default)
   {
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      { return await DiscoverCoreAsync(cancellationToken).ConfigureAwait(false); }
      finally { _gate.Release(); }
   }

   public async Task<QqqAuthenticationMetadata> GetAuthenticationMetadataAsync(CancellationToken cancellationToken = default)
   {
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      { return await AuthenticationCoreAsync(cancellationToken).ConfigureAwait(false); }
      finally { _gate.Release(); }
   }

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

   public async Task ResumeSessionAsync(CancellationToken cancellationToken = default)
   {
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      { await ResumeCoreAsync(cancellationToken).ConfigureAwait(false); }
      finally { _gate.Release(); }
   }

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

   public QqqSessionCredential ExportCredential() => new(Origin, _sessionToken ?? throw AuthenticationRequired());

   public async Task<QqqSessionBinding> GetSessionIdentityAsync(CancellationToken cancellationToken = default)
   {
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      { return await IdentityCoreAsync(_device, cancellationToken).ConfigureAwait(false); }
      finally { _gate.Release(); }
   }

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

   private async Task<HttpResponseMessage> SendCoreAsync(Uri uri, HttpMethod method, HttpContent? content, int maximumBytes, string? cookie,
      QqqDeviceCredential? device, string? basic, CancellationToken cancellationToken, bool providerDiscovery = false, bool streamContent = false, bool boundedByCaller = false)
   {
      if(!providerDiscovery && !SameOrigin(uri, Origin))
      { throw new ArgumentException("Cross-origin QQQ requests are forbidden.", nameof(uri)); }
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      if(!boundedByCaller)
      { timeout.CancelAfter(TimeSpan.FromSeconds(20)); }
      using var request = new HttpRequestMessage(method, uri) { Content = content };
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

   private void SetSession(string token)
   {
      if(!string.Equals(_sessionToken, token, StringComparison.Ordinal))
      { _generation++; }
      _sessionToken = token;
      _cookies = new CookieContainer();
      _cookies.Add(Origin, new Cookie("sessionUUID", token, "/") { HttpOnly = true, Secure = string.Equals(Origin.Scheme, "https", StringComparison.Ordinal) });
      _cookies.Add(Origin, new Cookie("sessionId", token, "/") { HttpOnly = true, Secure = string.Equals(Origin.Scheme, "https", StringComparison.Ordinal) });
   }

   private void ClearSession()
   {
      _sessionToken = null;
      _cookies = new CookieContainer();
      _device = null;
      _binding = null;
      _browserLogin = null;
      _generation++;
   }

   private string CookieHeader() => _cookies.GetCookieHeader(Origin);
   private void RequireAuthenticated() { if(_sessionToken is null) { throw AuthenticationRequired(); } }
   private static QqqSessionException AuthenticationRequired() => new(QqqSessionException.FailureKind.AuthenticationRequired, "Sign in to QQQ to continue.");
   private static QqqSessionException InvalidResponse() => new(QqqSessionException.FailureKind.InvalidResponse, "The server returned an invalid authentication or API response.");

   private void CheckAuthorization(HttpResponseMessage response)
   {
      if(response.StatusCode == HttpStatusCode.Unauthorized)
      { ClearSession(); throw AuthenticationRequired(); }
      if(response.StatusCode == HttpStatusCode.Forbidden)
      {
         throw new QqqSessionException(QqqSessionException.FailureKind.PermissionDenied, "The current QQQ user is not permitted to perform this operation.");
      }
   }

   private void RequireSuccess(HttpResponseMessage response)
   {
      CheckAuthorization(response);
      if(!response.IsSuccessStatusCode)
      {
         throw new QqqSessionException(QqqSessionException.FailureKind.TransportRefused, "The QQQ request did not succeed.");
      }
   }

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

   private static bool SameOrigin(Uri left, Uri right) => left.IsAbsoluteUri && right.IsAbsoluteUri
      && string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
      && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;

   private static StringContent JsonBody<T>(T body) => new(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

   private static T Parse<T>(HttpResponseMessage response)
   {
      using JsonDocument document = ParseDocument(response);
      try
      { return document.RootElement.Deserialize<T>(JsonOptions) ?? throw InvalidResponse(); }
      catch(JsonException) { throw InvalidResponse(); }
   }

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

   private static string IdentityValue(JsonElement root, string name)
   {
      if(!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String
         || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 1024 || value.GetString()!.Any(char.IsControl))
      {
         throw InvalidResponse();
      }
      return value.GetString()!;
   }

   public void Dispose()
   {
      ClearSession();
      _http.Dispose();
      _gate.Dispose();
   }
}
