/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.Cryptography;
using System.Text.Json;

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// Provider sign-out part of the session client. Signing out of the agent must also end the identity provider's browser session,
/// because the provider is meant to be invisible: leaving it signed in would let the next sign-in skip the credential prompt.
/// The server describes the provider's end-session request at logout, this client checks it against the provider's own discovery,
/// and the host opens it in the browser.
/// </summary>
public sealed partial class QqqSessionClient
{
   private const int IdTokenHintLimit = 32_768;



   /// <summary>
   /// Ends the QQQ session and, when the server offers provider sign-out for this exact callback, returns the provider's end-session
   /// request for the host to open in the browser. The request must name the issuer QQQ advertises, an end-session endpoint that is
   /// HTTPS on the issuer's origin and identical to the provider's own discovery, and the callback that was asked for; anything else
   /// is refused. A server without the capability gets the ordinary logout and null, and says so in the log. The local session is
   /// forgotten in every case, including failure.
   /// </summary>
   public async Task<QqqProviderLogout?> SignOutWithProviderAsync(Uri postLogoutRedirectUri, CancellationToken cancellationToken = default)
   {
      ArgumentNullException.ThrowIfNull(postLogoutRedirectUri);
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
         if(_sessionToken is null)
         {
            _diagnostics.Info("Sign-out requested with no held session; clearing local state only.");
            return null;
         }
         _diagnostics.Info($"Signing out of {Origin.Authority}: ending the server session and asking for the identity provider sign-out.");
         QqqAuthenticationMetadata metadata = await AuthenticationCoreAsync(cancellationToken).ConfigureAwait(false);
         QqqAuthenticationMetadata.NativeGrant? grant = metadata.Values?.NativeSessionLogin;
         string? unavailable = ProviderLogoutUnavailable(metadata, grant, postLogoutRedirectUri);
         using HttpResponseMessage response = await SendCoreAsync(new Uri(Origin, "/qqq/v1/logout"), HttpMethod.Post,
            unavailable is null ? JsonBody(new { nativeLogout = new { postLogoutRedirectUri = postLogoutRedirectUri.AbsoluteUri } }) : JsonBody(new { }),
            MetadataLimit, CookieHeader(), null, null, cancellationToken).ConfigureAwait(false);
         RequireSuccess(response);
         _diagnostics.Info("Server session ended.");
         if(unavailable is not null)
         {
            _diagnostics.Info("The identity provider session was NOT ended: " + unavailable + ".");
            return null;
         }
         return await ProviderLogoutFromAsync(response, grant!, postLogoutRedirectUri, cancellationToken).ConfigureAwait(false);
      }
      catch(Exception exception) when(ReportFailure("Provider sign-out", exception))
      { throw; }
      finally
      {
         ClearSession();
         _gate.Release();
      }
   }



   /// <summary>Says why provider sign-out cannot be requested (no capability, no listed callback, a password server), or null when it can.</summary>
   private string? ProviderLogoutUnavailable(QqqAuthenticationMetadata metadata, QqqAuthenticationMetadata.NativeGrant? grant, Uri redirect)
   {
      if(!string.Equals(metadata.Type, "OAUTH2", StringComparison.Ordinal) || grant is null)
      { return "this server signs in with a password, so there is no identity provider session"; }
      if(_discovery?.SupportsNativeSessionLogout != true)
      { return "the server does not advertise the nativeSessionLogout capability (UI API 1.10 or later)"; }
      QqqAuthenticationMetadata.NativeLogout? logout = metadata.Values?.NativeSessionLogout;
      if(logout is null || !string.Equals(logout.Flow, QqqAuthenticationMetadata.NativeLogout.RpInitiatedFlow, StringComparison.Ordinal))
      { return "the server publishes no OIDC_RP_INITIATED_LOGOUT descriptor"; }
      if(logout.PostLogoutRedirectUris is null || !logout.PostLogoutRedirectUris.Contains(redirect.AbsoluteUri, StringComparer.Ordinal))
      { return $"the server does not list {redirect.AbsoluteUri} as a post-logout callback"; }
      return null;
   }



   /// <summary>Reads and checks the provider data the server returned at logout, then builds the end-session request with a fresh state. Anything that does not match is refused without logging the hint.</summary>
   private async Task<QqqProviderLogout?> ProviderLogoutFromAsync(HttpResponseMessage response, QqqAuthenticationMetadata.NativeGrant grant, Uri redirect, CancellationToken cancellationToken)
   {
      using JsonDocument document = ParseDocument(response);
      if(!document.RootElement.TryGetProperty("nativeLogoutData", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
      {
         _diagnostics.Info("The identity provider session was NOT ended: the server returned no provider sign-out data (an older session, or none was stored).");
         return null;
      }
      string? issuer = StringProperty(data, "issuer");
      string? endpoint = StringProperty(data, "endSessionEndpoint");
      string? callback = StringProperty(data, "postLogoutRedirectUri");
      string? hint = StringProperty(data, "idTokenHint");
      if(!string.Equals(issuer, grant.Issuer, StringComparison.Ordinal) || !string.Equals(callback, redirect.AbsoluteUri, StringComparison.Ordinal)
         || string.IsNullOrEmpty(hint) || hint.Length > IdTokenHintLimit || hint.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
         || !TryHttps(endpoint, out Uri? endSession) || !SameOrigin(endSession!, new Uri(grant.Issuer)))
      {
         _diagnostics.Info("Provider sign-out refused: the data the server returned does not match the issuer, callback or HTTPS end-session endpoint it advertised.");
         throw InvalidResponse();
      }
      Uri discovered = await ProviderEndSessionEndpointAsync(grant, cancellationToken).ConfigureAwait(false);
      if(!string.Equals(discovered.AbsoluteUri, endSession!.AbsoluteUri, StringComparison.Ordinal))
      {
         _diagnostics.Info("Provider sign-out refused: the end-session endpoint from the server differs from the one in the identity provider's own discovery.");
         throw InvalidResponse();
      }
      string state = Base64Url(RandomNumberGenerator.GetBytes(32));
      var query = new Dictionary<string, string>(StringComparer.Ordinal)
      {
         ["id_token_hint"] = hint,
         ["post_logout_redirect_uri"] = redirect.AbsoluteUri,
         ["state"] = state,
      };
      var builder = new UriBuilder(endSession)
      {
         Query = string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))),
      };
      _diagnostics.Info($"Provider sign-out ready: the end-session page is {QqqSessionDiagnostics.Describe(builder.Uri)} (query not logged).");
      return new QqqProviderLogout(builder.Uri, redirect, state);
   }



   /// <summary>Reads a string property of a JSON object, or null when it is missing or not a string.</summary>
   private static string? StringProperty(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;



   /// <summary>
   /// Fetches the identity provider's discovery document and returns its end-session endpoint, after the same issuer, HTTPS and
   /// same-origin checks the authorization endpoint gets. A provider without one cannot be signed out of from here.
   /// </summary>
   private async Task<Uri> ProviderEndSessionEndpointAsync(QqqAuthenticationMetadata.NativeGrant grant, CancellationToken cancellationToken)
   {
      _diagnostics.Info($"Fetching the identity provider's discovery document from {QqqSessionDiagnostics.Describe(new Uri(grant.DiscoveryUrl))} for its end-session endpoint.");
      using HttpResponseMessage response = await SendCoreAsync(new Uri(grant.DiscoveryUrl), HttpMethod.Get, null, MetadataLimit,
         null, null, null, cancellationToken, providerDiscovery: true).ConfigureAwait(false);
      RequireSuccess(response);
      using JsonDocument document = ParseDocument(response);
      string? providerIssuer = StringProperty(document.RootElement, "issuer");
      string? endpoint = StringProperty(document.RootElement, "end_session_endpoint");
      if(!string.Equals(providerIssuer, grant.Issuer, StringComparison.Ordinal) || !TryHttps(endpoint, out Uri? endSession) || !SameOrigin(endSession!, new Uri(grant.Issuer)))
      {
         _diagnostics.Info("Provider sign-out refused: the provider's discovery names a different issuer or no HTTPS end-session endpoint on the issuer's origin.");
         throw QqqApiDiscovery.Configuration();
      }
      return endSession!;
   }
}
