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
         _diagnostics.Info($"Browser sign-in requested with callback {redirectUri.AbsoluteUri}.");
         QqqAuthenticationMetadata metadata = await AuthenticationCoreAsync(cancellationToken).ConfigureAwait(false);
         QqqAuthenticationMetadata.NativeGrant grant = ValidateNativeGrant(metadata);
         RequireListedRedirect(grant, redirectUri);
         Uri authorizationUri = await ProviderAuthorizationEndpointAsync(grant, cancellationToken).ConfigureAwait(false);
         string state = Base64Url(RandomNumberGenerator.GetBytes(32));
         string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
         string nonce = Base64Url(RandomNumberGenerator.GetBytes(32));
         var query = new Dictionary<string, string>(StringComparer.Ordinal)
         {
            ["client_id"] = metadata.Values!.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = metadata.Values.Scopes,
            ["state"] = state,
            ["nonce"] = nonce,
            ["prompt"] = grant.Prompt,
            // prompt=login alone was seen reusing a retained provider session on real Authentik; max_age=0 makes any session too old.
            ["max_age"] = "0",
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
         };
         var builder = new UriBuilder(authorizationUri)
         {
            Query = string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))),
         };
         ClearSession();
         string? nonceSignal = NonceSignal(grant);
         _diagnostics.Info(nonceSignal is null
            ? "The server does not validate the sign-in nonce at exchange (legacy server); the exchange body stays unchanged."
            : $"The server validates the sign-in nonce at exchange ({nonceSignal}); this attempt's nonce will be sent as expectedNonce.");
         // The per-attempt nonce is kept only when the server validates it at exchange; legacy servers keep the exact old exchange.
         _browserLogin = new QqqBrowserLogin(builder.Uri, redirectUri, state, verifier, grant.Issuer, _timeProvider.GetTimestamp(), nonceSignal is null ? null : nonce);
         _diagnostics.Info($"Browser sign-in attempt started: the authorization page is {ShownText(QqqSessionDiagnostics.Describe(builder.Uri))} (query not logged); waiting for the callback, which expires after five minutes.");
         return _browserLogin;
      }
      catch(Exception exception)
      {
         ReportFailure("Browser sign-in start", exception);
         throw;
      }
      finally { _gate.Release(); }
   }



   /// <summary>Refuses a callback address that is not an app link or private scheme, or that the server's native grant does not list exactly, saying which in the log.</summary>
   private void RequireListedRedirect(QqqAuthenticationMetadata.NativeGrant grant, Uri redirectUri)
   {
      if(!ValidRedirect(redirectUri))
      {
         _diagnostics.Info($"Browser sign-in refused locally: the callback {redirectUri.AbsoluteUri} is not an HTTPS app link or dotted private scheme.");
         throw QqqApiDiscovery.Configuration();
      }
      if(!grant.RedirectUris.Contains(redirectUri.AbsoluteUri, StringComparer.Ordinal))
      {
         _diagnostics.Info($"Browser sign-in refused: the server's native grant does not list the callback {redirectUri.AbsoluteUri}; it lists [{ShownAddresses(grant.RedirectUris)}]. Add this callback to the QQQ native session grant.");
         throw QqqApiDiscovery.Configuration();
      }
   }



   /// <summary>
   /// Fetches the identity provider's discovery document and returns its authorization endpoint, after checking that the
   /// provider names the same issuer QQQ advertises, an HTTPS endpoint on that issuer's origin, and S256 support. Each
   /// shortfall is logged before the generic configuration failure is thrown.
   /// </summary>
   private async Task<Uri> ProviderAuthorizationEndpointAsync(QqqAuthenticationMetadata.NativeGrant grant, CancellationToken cancellationToken)
   {
      _diagnostics.Info($"Fetching the identity provider's discovery document from {ShownText(QqqSessionDiagnostics.Describe(new Uri(grant.DiscoveryUrl)))}.");
      using HttpResponseMessage response = await SendCoreAsync(new Uri(grant.DiscoveryUrl), HttpMethod.Get, null, MetadataLimit,
         null, null, null, cancellationToken, providerDiscovery: true).ConfigureAwait(false);
      RequireSuccess(response);
      using JsonDocument document = ParseDocument(response);
      JsonElement provider = document.RootElement;
      string? providerIssuer = provider.TryGetProperty("issuer", out JsonElement issuer) && issuer.ValueKind == JsonValueKind.String ? issuer.GetString() : null;
      string? providerAuthorization = provider.TryGetProperty("authorization_endpoint", out JsonElement authorization) && authorization.ValueKind == JsonValueKind.String ? authorization.GetString() : null;
      bool s256 = provider.TryGetProperty("code_challenge_methods_supported", out JsonElement methods) && methods.ValueKind == JsonValueKind.Array
         && methods.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), "S256", StringComparison.Ordinal));
      _diagnostics.Info($"Provider discovery: issuer {ShownAddress(providerIssuer)}, authorization endpoint {ShownAddress(providerAuthorization)}, S256 {(s256 ? "supported" : "not listed")}.");
      if(!string.Equals(providerIssuer, grant.Issuer, StringComparison.Ordinal))
      {
         _diagnostics.Info($"Browser sign-in refused: the provider's issuer '{ShownText(providerIssuer)}' does not exactly match the issuer QQQ advertises '{ShownText(grant.Issuer)}'.");
         throw QqqApiDiscovery.Configuration();
      }
      if(!TryHttps(providerAuthorization, out Uri? authorizationUri) || !SameOrigin(authorizationUri!, new Uri(grant.Issuer)))
      {
         _diagnostics.Info("Browser sign-in refused: the provider's authorization endpoint is not an HTTPS address on the issuer's origin.");
         throw QqqApiDiscovery.Configuration();
      }
      if(!s256)
      {
         _diagnostics.Info("Browser sign-in refused: the provider does not list S256 among its supported code challenge methods.");
         throw QqqApiDiscovery.Configuration();
      }
      return authorizationUri!;
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
         string code = AcceptCallback(pending, attempt, callback);
         _diagnostics.Info("Browser callback validated; exchanging the authorization code with QQQ for a server-managed session.");
         using HttpResponseMessage response = await SendCoreAsync(new Uri(Origin, "/qqq/v1/manageSession"), HttpMethod.Post,
            JsonBody(NativeCodeExchange(attempt, code)),
            MetadataLimit, null, null, null, cancellationToken).ConfigureAwait(false);
         RequireSuccess(response);
         AdoptManagedSession(response);
         _diagnostics.Info("Browser sign-in accepted; a server-managed session is now held.");
      }
      catch(Exception exception)
      {
         ReportFailure("Browser sign-in completion", exception);
         throw;
      }
      finally { _gate.Release(); }
   }



   /// <summary>
   /// Checks a browser callback against the attempt it claims to finish and returns its authorization code. The callback must
   /// belong to the pending attempt, arrive within five minutes, be exactly the registered redirect with no fragment or user
   /// information, carry no provider error, the attempt's own state, a usable code and, when it names one, the expected issuer.
   /// Each refusal is logged with its reason and reported as the same generic invalid response.
   /// </summary>
   private string AcceptCallback(QqqBrowserLogin? pending, QqqBrowserLogin attempt, Uri callback)
   {
      TimeSpan age = _timeProvider.GetElapsedTime(attempt.StartedAt);
      _diagnostics.Info($"Browser callback received at {ShownText(QqqSessionDiagnostics.Describe(callback))} after {age.TotalSeconds:0} s.");
      if(!ReferenceEquals(pending, attempt))
      {
         _diagnostics.Info("Browser callback refused: it does not belong to the pending sign-in attempt (a new attempt was started, or the attempt was already used).");
         throw InvalidResponse();
      }
      if(age < TimeSpan.Zero || age > TimeSpan.FromMinutes(5))
      {
         _diagnostics.Info("Browser callback refused: the attempt is older than five minutes. Start the browser sign-in again.");
         throw InvalidResponse();
      }
      if(!callback.IsAbsoluteUri || callback.Fragment.Length != 0 || callback.UserInfo.Length != 0
         || !string.Equals(callback.GetLeftPart(UriPartial.Path), attempt.RedirectUri.AbsoluteUri, StringComparison.Ordinal))
      {
         _diagnostics.Info($"Browser callback refused: its address does not match the registered callback {attempt.RedirectUri.AbsoluteUri} exactly, or it carries a fragment or user information.");
         throw InvalidResponse();
      }
      Dictionary<string, string> values = CallbackValues(callback);
      _diagnostics.Debug(() => $"Browser callback parameters: [{ShownList(values.Keys)}] (values not logged).");
      if(values.TryGetValue("error", out string? providerError))
      {
         _diagnostics.Info($"Browser sign-in refused by the identity provider: error '{Shown(providerError)}'{(values.TryGetValue("error_description", out string? description) ? " (" + ShownText(description) + ")" : "")}.");
         throw InvalidResponse();
      }
      if(!values.TryGetValue("state", out string? state) || !string.Equals(state, attempt.State, StringComparison.Ordinal))
      {
         _diagnostics.Info("Browser callback refused: the state parameter is missing or does not match this attempt.");
         throw InvalidResponse();
      }
      if(!values.TryGetValue("code", out string? code) || string.IsNullOrWhiteSpace(code) || code.Length > 4096)
      {
         _diagnostics.Info("Browser callback refused: no usable authorization code was returned.");
         throw InvalidResponse();
      }
      if(values.TryGetValue("iss", out string? issuer) && !string.Equals(issuer, attempt.Issuer, StringComparison.Ordinal))
      {
         _diagnostics.Info($"Browser callback refused: the issuer '{ShownText(issuer)}' differs from the expected '{ShownText(attempt.Issuer)}'.");
         throw InvalidResponse();
      }
      return code;
   }



   /// <summary>
   /// Says why the exchange will carry the attempt's nonce, or null for a legacy server: the grant's policy REQUIRED, the grant's
   /// policy OPTIONAL (the server validates a nonce when one is sent, so sending it is always right), or the discovery
   /// capability nativeSessionNonceValidation on a server whose grant does not state a policy.
   /// </summary>
   private string? NonceSignal(QqqAuthenticationMetadata.NativeGrant grant)
   {
      if(string.Equals(grant.NonceValidation, QqqAuthenticationMetadata.NativeGrant.NonceRequired, StringComparison.Ordinal))
      { return "grant policy REQUIRED"; }
      if(string.Equals(grant.NonceValidation, QqqAuthenticationMetadata.NativeGrant.NonceOptional, StringComparison.Ordinal))
      { return "grant policy OPTIONAL"; }
      return _discovery?.ValidatesNativeSessionNonce == true ? "discovery capability " + QqqApiDiscovery.NonceValidationFeature : null;
   }



   /// <summary>
   /// Builds the manageSession code-exchange body. A legacy attempt (no stored nonce) keeps the exact four-field body; an attempt
   /// made under the server's REQUIRED policy adds that attempt's own nonce as expectedNonce. The nonce comes only from the attempt,
   /// never from the callback, and is not logged.
   /// </summary>
   private static object NativeCodeExchange(QqqBrowserLogin attempt, string code) => attempt.ExpectedNonce is null
      ? new { code, codeVerifier = attempt.Verifier, redirectUri = attempt.RedirectUri.AbsoluteUri, authenticationFlow = QqqAuthenticationMetadata.NativeSessionFlow }
      : new { code, codeVerifier = attempt.Verifier, redirectUri = attempt.RedirectUri.AbsoluteUri, authenticationFlow = QqqAuthenticationMetadata.NativeSessionFlow, expectedNonce = attempt.ExpectedNonce };



   /// <summary>
   /// Checks that the server's native-session grant is one this agent can use safely: an HTTPS server with OAuth2, the expected
   /// flow, S256 and a forced login prompt, HTTPS issuer and discovery addresses, a client identifier, the openid scope, and a
   /// bounded list of valid redirect addresses. Anything else is reported as a server configuration problem.
   /// </summary>
   private QqqAuthenticationMetadata.NativeGrant ValidateNativeGrant(QqqAuthenticationMetadata metadata)
   {
      _discovery!.Validate(nativeSession: true, report: _diagnostics.Info);
      List<string> problems = NativeGrantProblems(metadata);
      if(problems.Count != 0)
      {
         _diagnostics.Info("Native browser sign-in grant rejected: " + string.Join("; ", problems) + ".");
         throw QqqApiDiscovery.Configuration();
      }
      QqqAuthenticationMetadata.NativeGrant grant = metadata.Values!.NativeSessionLogin!;
      _diagnostics.Info($"Native browser sign-in grant accepted: issuer {ShownAddress(grant.Issuer)}, {grant.RedirectUris.Count} redirect address(es).");
      return grant;
   }



   /// <summary>
   /// Lists, in plain words, every reason the published native-session grant cannot be used; an empty list means it can.
   /// The sign-in path throws on a non-empty list and the compatibility check reports it, so both judge the grant by the
   /// same rules. The reasons are shown to an operator before anyone has signed in, so every value the server supplied
   /// is quoted through <see cref="QqqCompatibilityProblem.Printable"/>, never raw.
   /// </summary>
   private List<string> NativeGrantProblems(QqqAuthenticationMetadata metadata)
   {
      QqqAuthenticationMetadata.NativeGrant? grant = metadata.Values?.NativeSessionLogin;
      var problems = new List<string>();
      if(!string.Equals(Origin.Scheme, "https", StringComparison.Ordinal))
      { problems.Add("the server origin is not HTTPS"); }
      if(!string.Equals(metadata.Type, "OAUTH2", StringComparison.Ordinal))
      { problems.Add($"authentication type is '{QqqCompatibilityProblem.Printable(metadata.Type)}', not OAUTH2"); }
      if(grant is null)
      { problems.Add("no nativeSessionLogin grant is published"); }
      else
      { AddGrantProblems(grant, problems); }
      if(string.IsNullOrWhiteSpace(metadata.Values?.ClientId) || metadata.Values.ClientId.Length > 1024)
      { problems.Add("client id is missing or too long"); }
      if(string.IsNullOrWhiteSpace(metadata.Values?.Scopes) || !metadata.Values.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid", StringComparer.Ordinal))
      { problems.Add($"scopes '{QqqCompatibilityProblem.Printable(metadata.Values?.Scopes)}' do not include openid"); }
      return problems;
   }



   /// <summary>Adds the shortfalls of the grant itself: flow, challenge method, prompt, nonce policy, issuer and discovery addresses, and the redirect list.</summary>
   private static void AddGrantProblems(QqqAuthenticationMetadata.NativeGrant grant, List<string> problems)
   {
      if(!string.Equals(grant.Flow, QqqAuthenticationMetadata.NativeSessionFlow, StringComparison.Ordinal))
      { problems.Add($"grant flow is '{QqqCompatibilityProblem.Printable(grant.Flow)}', expected {QqqAuthenticationMetadata.NativeSessionFlow}"); }
      if(!string.Equals(grant.CodeChallengeMethod, "S256", StringComparison.Ordinal))
      { problems.Add($"code challenge method is '{QqqCompatibilityProblem.Printable(grant.CodeChallengeMethod)}', expected S256"); }
      if(!string.Equals(grant.Prompt, "login", StringComparison.Ordinal))
      { problems.Add($"prompt is '{QqqCompatibilityProblem.Printable(grant.Prompt)}', expected login"); }
      // Absent means a legacy server; REQUIRED and OPTIONAL both mean the nonce is sent; any other value is a policy this client cannot honor, so refuse before any provider or browser step.
      if(!string.IsNullOrEmpty(grant.NonceValidation) && !string.Equals(grant.NonceValidation, QqqAuthenticationMetadata.NativeGrant.NonceRequired, StringComparison.Ordinal)
         && !string.Equals(grant.NonceValidation, QqqAuthenticationMetadata.NativeGrant.NonceOptional, StringComparison.Ordinal))
      { problems.Add($"nonce validation policy '{QqqCompatibilityProblem.Printable(grant.NonceValidation)}' is not one this client supports (expected REQUIRED, OPTIONAL or absent)"); }
      if(!TryHttps(grant.Issuer, out _))
      { problems.Add($"issuer '{QqqCompatibilityProblem.Printable(grant.Issuer)}' is not a plain HTTPS address"); }
      if(!TryHttps(grant.DiscoveryUrl, out _))
      { problems.Add($"discovery address '{QqqCompatibilityProblem.Printable(grant.DiscoveryUrl)}' is not a plain HTTPS address"); }
      if(grant.RedirectUris is null || grant.RedirectUris.Count is < 1 or > 32)
      {
         problems.Add($"{grant.RedirectUris?.Count ?? 0} redirect address(es) listed; expected 1 to 32");
         return;
      }
      string[] invalid = [.. grant.RedirectUris.Where(value => !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || !ValidRedirect(uri))];
      if(invalid.Length != 0)
      { problems.Add("redirect address(es) not an HTTPS app link or dotted private scheme: " + QqqCompatibilityProblem.PrintableList(invalid)); }
   }



   /// <summary>
   /// Parses an address and accepts it only when it is an absolute HTTPS address that has an origin (see
   /// <see cref="QqqOrigin.Of"/>) and also passes the configuration-address safety checks. Every address that comes
   /// through here is text a server supplied, and each is later compared with another, shown, or connected to. A
   /// host the platform cannot name in ASCII (one that holds a line separator) has no origin, and the platform
   /// throws when asked to connect to it, so such an address is refused here, where the caller reports a refusal,
   /// and never reaches a request.
   /// </summary>
   private static bool TryHttps(string? value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri)
      && string.Equals(uri.Scheme, "https", StringComparison.Ordinal) && QqqOrigin.Of(uri) is not null && SafeConfigurationUri(uri);



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
