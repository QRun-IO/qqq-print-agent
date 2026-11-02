/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// Browser sign-in part of the session client: authorization code with PKCE against the provider the server advertises. The
/// server stays in charge of the session; this client only proves the exchange, checks the provider's own discovery, and never
/// keeps the code, the verifier or a provider token beyond the one attempt.
/// </summary>
public sealed partial class QqqSessionClient
{
   /// <summary>
   /// Starts a browser sign-in for an approved redirect address. The server's native grant must be valid and list that exact
   /// redirect, and the provider's discovery document must name the same issuer, an HTTPS authorization endpoint on the issuer's
   /// origin and S256 support. It then builds the authorization address with fresh random state, nonce and PKCE challenge,
   /// clears any current session, and remembers the attempt so only its callback can complete it.
   /// </summary>
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



   /// <summary>
   /// Finishes a browser sign-in from the provider's redirect. The attempt is consumed whatever happens, so a callback can be
   /// used once; it must be the pending attempt, less than five minutes old, with no fragment or credentials and exactly the
   /// registered redirect. The state must match, a provider error or a different issuer is refused, and only then is the code,
   /// with its PKCE verifier, exchanged for a server-managed session.
   /// </summary>
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



   /// <summary>
   /// Checks that the server's native-session grant is one this agent can use safely: an HTTPS server with OAuth2, the expected
   /// flow, S256 and a forced login prompt, HTTPS issuer and discovery addresses, a client identifier, the openid scope, and a
   /// bounded list of valid redirect addresses. Anything else is reported as a server configuration problem.
   /// </summary>
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



   /// <summary>Parses an address and accepts it only when it is an absolute HTTPS address with a host that also passes the configuration-address safety checks.</summary>
   private static bool TryHttps(string? value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri)
      && string.Equals(uri.Scheme, "https", StringComparison.Ordinal) && uri.Host.Length != 0 && SafeConfigurationUri(uri);



   /// <summary>Rejects addresses carrying credentials, a query, a fragment, port zero, wildcards or backslashes, or longer than 2048 characters, so server-supplied configuration cannot smuggle extra meaning into a URL.</summary>
   private static bool SafeConfigurationUri(Uri uri) => uri.IsAbsoluteUri && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
      && uri.Port != 0 && uri.AbsoluteUri.Length <= 2048 && !uri.OriginalString.Contains('*', StringComparison.Ordinal) && !uri.OriginalString.Contains('\\', StringComparison.Ordinal);



   /// <summary>Accepts a redirect address only if it is an HTTPS app link or a dotted private scheme (never plain http), as the server's own native-session grant requires.</summary>
   // Mirror the central native-session grant: HTTPS app links or explicit dotted private schemes; no inferred loopback exception.
   private static bool ValidRedirect(Uri uri) => SafeConfigurationUri(uri) && uri.AbsolutePath.Length != 0
      && (string.Equals(uri.Scheme, "https", StringComparison.Ordinal)
         || (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) && uri.Scheme.Contains('.', StringComparison.Ordinal)));



   /// <summary>Splits the callback query into decoded name and value pairs, refusing an oversize query, a malformed pair, control characters or a repeated name, because a repeated parameter could make two readers disagree about the code or state.</summary>
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



   /// <summary>Encodes bytes as unpadded URL-safe base64, the form PKCE and the state parameter require.</summary>
   private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
