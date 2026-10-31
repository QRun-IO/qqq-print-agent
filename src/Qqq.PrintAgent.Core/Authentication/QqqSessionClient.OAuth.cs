/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Qqq.PrintAgent.Core.Authentication;

public sealed partial class QqqSessionClient
{
   public async Task<QqqBrowserLogin> BeginBrowserLoginAsync(Uri redirectUri, CancellationToken cancellationToken = default)
   {
      ArgumentNullException.ThrowIfNull(redirectUri);
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
         QqqAuthenticationMetadata metadata = await AuthenticationCoreAsync(cancellationToken).ConfigureAwait(false);
         QqqAuthenticationMetadata.NativeGrant grant = ValidateNativeGrant(metadata);
         if(!ValidRedirect(redirectUri) || !grant.RedirectUris.Contains(redirectUri.AbsoluteUri, StringComparer.Ordinal))
         { throw QqqApiDiscovery.Configuration(); }
         using HttpResponseMessage response = await SendCoreAsync(new Uri(grant.DiscoveryUrl), HttpMethod.Get, null, MetadataLimit,
            null, null, null, cancellationToken, providerDiscovery: true).ConfigureAwait(false);
         RequireSuccess(response);
         using JsonDocument document = ParseDocument(response);
         JsonElement provider = document.RootElement;
         if(!provider.TryGetProperty("issuer", out JsonElement issuer) || issuer.ValueKind != JsonValueKind.String
            || !string.Equals(issuer.GetString(), grant.Issuer, StringComparison.Ordinal)
            || !provider.TryGetProperty("authorization_endpoint", out JsonElement authorization) || authorization.ValueKind != JsonValueKind.String
            || !TryHttps(authorization.GetString(), out Uri? authorizationUri)
            || !SameOrigin(authorizationUri!, new Uri(grant.Issuer))
            || !provider.TryGetProperty("code_challenge_methods_supported", out JsonElement methods) || methods.ValueKind != JsonValueKind.Array
            || !methods.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), "S256", StringComparison.Ordinal)))
         {
            throw QqqApiDiscovery.Configuration();
         }
         string state = Base64Url(RandomNumberGenerator.GetBytes(32));
         string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
         var query = new Dictionary<string, string>(StringComparer.Ordinal)
         {
            ["client_id"] = metadata.Values!.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = metadata.Values.Scopes,
            ["state"] = state,
            ["nonce"] = Base64Url(RandomNumberGenerator.GetBytes(32)),
            ["prompt"] = grant.Prompt,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
         };
         var builder = new UriBuilder(authorizationUri!)
         {
            Query = string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))),
         };
         ClearSession();
         _browserLogin = new QqqBrowserLogin(builder.Uri, redirectUri, state, verifier, grant.Issuer, _timeProvider.GetTimestamp());
         return _browserLogin;
      }
      finally { _gate.Release(); }
   }

   public async Task CompleteBrowserLoginAsync(QqqBrowserLogin attempt, Uri callback, CancellationToken cancellationToken = default)
   {
      ArgumentNullException.ThrowIfNull(attempt);
      ArgumentNullException.ThrowIfNull(callback);
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
         QqqBrowserLogin? pending = _browserLogin;
         _browserLogin = null;
         TimeSpan age = _timeProvider.GetElapsedTime(attempt.StartedAt);
         if(!ReferenceEquals(pending, attempt)
            || age < TimeSpan.Zero || age > TimeSpan.FromMinutes(5)
            || !callback.IsAbsoluteUri || callback.Fragment.Length != 0 || callback.UserInfo.Length != 0
            || !string.Equals(callback.GetLeftPart(UriPartial.Path), attempt.RedirectUri.AbsoluteUri, StringComparison.Ordinal))
         {
            throw InvalidResponse();
         }
         Dictionary<string, string> values = CallbackValues(callback);
         if(values.ContainsKey("error") || !values.TryGetValue("state", out string? state) || !string.Equals(state, attempt.State, StringComparison.Ordinal)
            || !values.TryGetValue("code", out string? code) || string.IsNullOrWhiteSpace(code) || code.Length > 4096
            || (values.TryGetValue("iss", out string? issuer) && !string.Equals(issuer, attempt.Issuer, StringComparison.Ordinal)))
         {
            throw InvalidResponse();
         }
         using HttpResponseMessage response = await SendCoreAsync(new Uri(Origin, "/qqq/v1/manageSession"), HttpMethod.Post,
            JsonBody(new { code, codeVerifier = attempt.Verifier, redirectUri = attempt.RedirectUri.AbsoluteUri, authenticationFlow = QqqAuthenticationMetadata.NativeSessionFlow }),
            MetadataLimit, null, null, null, cancellationToken).ConfigureAwait(false);
         RequireSuccess(response);
         AdoptManagedSession(response);
      }
      finally { _gate.Release(); }
   }

   private QqqAuthenticationMetadata.NativeGrant ValidateNativeGrant(QqqAuthenticationMetadata metadata)
   {
      _discovery!.Validate(nativeSession: true);
      QqqAuthenticationMetadata.NativeGrant? grant = metadata.Values?.NativeSessionLogin;
      if(!string.Equals(Origin.Scheme, "https", StringComparison.Ordinal) || !string.Equals(metadata.Type, "OAUTH2", StringComparison.Ordinal)
         || grant is null || !string.Equals(grant.Flow, QqqAuthenticationMetadata.NativeSessionFlow, StringComparison.Ordinal)
         || !string.Equals(grant.CodeChallengeMethod, "S256", StringComparison.Ordinal) || !string.Equals(grant.Prompt, "login", StringComparison.Ordinal)
         || !TryHttps(grant.Issuer, out _) || !TryHttps(grant.DiscoveryUrl, out _)
         || string.IsNullOrWhiteSpace(metadata.Values!.ClientId) || metadata.Values.ClientId.Length > 1024
         || string.IsNullOrWhiteSpace(metadata.Values.Scopes) || !metadata.Values.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid", StringComparer.Ordinal)
         || grant.RedirectUris is null || grant.RedirectUris.Count is < 1 or > 32
         || grant.RedirectUris.Any(value => !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || !ValidRedirect(uri)))
      {
         throw QqqApiDiscovery.Configuration();
      }
      return grant;
   }

   private static bool TryHttps(string? value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri)
      && string.Equals(uri.Scheme, "https", StringComparison.Ordinal) && uri.Host.Length != 0 && SafeConfigurationUri(uri);

   private static bool SafeConfigurationUri(Uri uri) => uri.IsAbsoluteUri && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
      && uri.Port != 0 && uri.AbsoluteUri.Length <= 2048 && !uri.OriginalString.Contains('*', StringComparison.Ordinal) && !uri.OriginalString.Contains('\\', StringComparison.Ordinal);

   // Mirror the central native-session grant: HTTPS app links or explicit dotted private schemes; no inferred loopback exception.
   private static bool ValidRedirect(Uri uri) => SafeConfigurationUri(uri) && uri.AbsolutePath.Length != 0
      && (string.Equals(uri.Scheme, "https", StringComparison.Ordinal)
         || (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) && uri.Scheme.Contains('.', StringComparison.Ordinal)));

   private static Dictionary<string, string> CallbackValues(Uri callback)
   {
      if(callback.Query.Length is < 2 or > 16_384)
      { throw InvalidResponse(); }
      var values = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach(string entry in callback.Query[1..].Split('&'))
      {
         string[] pair = entry.Split('=', 2);
         if(pair.Length != 2)
         { throw InvalidResponse(); }
         string name = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
         string value = Uri.UnescapeDataString(pair[1].Replace('+', ' '));
         if(name.Any(char.IsControl) || value.Any(char.IsControl) || !values.TryAdd(name, value))
         { throw InvalidResponse(); }
      }
      return values;
   }

   private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
